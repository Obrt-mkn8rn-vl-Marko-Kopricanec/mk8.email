using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class Pop3ServerService(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig environment,
    ILogger<Pop3ServerService> logger) : BackgroundService
{
    private const int MaximumCommandLineCharacters = 510;
    private const int MaximumAuthenticationLineCharacters = 4096;
    private const int MaximumConcurrentConnections = 256;
    private const int MaximumAuthenticationFailures = 5;

    private enum ListenerMode { StartTls, ImplicitTls }
    private enum Pop3State { Authorization, Transaction, Update }
    private enum SessionUpgrade { None, StartTls }

    private sealed record Pop3Message(int Number, Guid Id, int Uid, int SizeBytes)
    {
        public string UniqueId => $"E{Id:N}";
    }

    private sealed class Pop3Session
    {
        public bool IsSecure { get; set; }
        public Pop3State State { get; set; }
        public string? PendingUsername { get; set; }
        public Guid? UserId { get; set; }
        public Guid? LockedUserId { get; set; }
        public int AuthenticationFailures { get; set; }
        public required string RemoteIp { get; init; }
        public List<Pop3Message> Messages { get; } = [];
        public HashSet<Guid> DeletedMessageIds { get; } = [];
    }

    private readonly ConnectionLimiter _connectionLimiter = new(MaximumConcurrentConnections);
    private readonly ConcurrentDictionary<Guid, byte> _activeMaildrops = new();
    private readonly SemaphoreSlim _retrievalLimiter = new(4, 4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        if (environment.Pop3.EnablePop3)
        {
            tasks.Add(ListenAsync(
                environment.Pop3.Port,
                ListenerMode.StartTls,
                stoppingToken));
        }
        if (environment.Pop3.EnableImplicitTls)
        {
            tasks.Add(ListenAsync(
                environment.Pop3.ImplicitTlsPort,
                ListenerMode.ImplicitTls,
                stoppingToken));
        }

        if (tasks.Count == 0)
        {
            logger.LogWarning("No POP3 listeners are enabled.");
            return;
        }

        await Task.WhenAll(tasks);
    }

    private async Task ListenAsync(int port, ListenerMode mode, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        logger.LogInformation("POP3 {Mode} listener started on port {Port}", mode, port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleConnectionAsync(client, mode, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            logger.LogInformation("POP3 {Mode} listener on port {Port} stopped", mode, port);
        }
    }

    private async Task HandleConnectionAsync(
        TcpClient client,
        ListenerMode mode,
        CancellationToken cancellationToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint;
        var remoteAddress = (remoteEndpoint as IPEndPoint)?.Address ?? IPAddress.None;
        var remoteLabel = remoteEndpoint?.ToString() ?? "unknown";
        using var connectionLease = _connectionLimiter.TryAcquire(
            remoteAddress,
            environment.Limits.MaxConnectionsPerIp);
        if (connectionLease is null)
        {
            logger.LogWarning("Rejected POP3 connection from {Endpoint}: connection limit", remoteLabel);
            client.Dispose();
            return;
        }

        var session = new Pop3Session
        {
            IsSecure = mode == ListenerMode.ImplicitTls,
            RemoteIp = remoteAddress.ToString(),
        };

        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(environment.Limits.ConnectionTimeoutSeconds));
                Stream stream = client.GetStream();

                if (mode == ListenerMode.ImplicitTls)
                {
                    if (environment.Tls.CertificatePath is null)
                        throw new InvalidOperationException("Implicit POP3 TLS requires a certificate.");

                    using var certificate = LoadCertificate();
                    var tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    if (!await TryAuthenticateAsServerAsync(
                            tlsStream,
                            certificate,
                            timeout.Token,
                            remoteLabel))
                    {
                        await tlsStream.DisposeAsync();
                        return;
                    }
                    stream = tlsStream;
                }

                var sendGreeting = true;
                SessionUpgrade upgrade;
                do
                {
                    upgrade = await RunSessionAsync(stream, session, timeout, sendGreeting);
                    sendGreeting = false;

                    if (upgrade == SessionUpgrade.StartTls)
                    {
                        using var certificate = LoadCertificate();
                        var tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                        if (!await TryAuthenticateAsServerAsync(
                                tlsStream,
                                certificate,
                                timeout.Token,
                                remoteLabel))
                        {
                            await tlsStream.DisposeAsync();
                            return;
                        }
                        stream = tlsStream;
                        session.IsSecure = true;
                        session.PendingUsername = null;
                    }
                } while (upgrade != SessionUpgrade.None && session.State != Pop3State.Update);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("POP3 connection from {Endpoint} timed out", remoteLabel);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Error handling POP3 connection from {Endpoint}", remoteLabel);
        }
        finally
        {
            ReleaseMaildrop(session);
        }
    }

    private async Task<SessionUpgrade> RunSessionAsync(
        Stream stream,
        Pop3Session session,
        CancellationTokenSource timeout,
        bool sendGreeting)
    {
        var cancellationToken = timeout.Token;
        using var streamReader = new StreamReader(
            stream,
            MailWireEncoding.Instance,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var reader = new BoundedLineReader(streamReader);
        await using var writer = new StreamWriter(
            stream,
            MailWireEncoding.Instance,
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };

        if (sendGreeting)
            await writer.WriteLineAsync($"+OK {environment.Smtp.Hostname} mk8.email POP3 ready");

        while (!timeout.IsCancellationRequested && session.State != Pop3State.Update)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(environment.Limits.ConnectionTimeoutSeconds));
            var lineResult = await reader.ReadLineAsync(
                MaximumCommandLineCharacters,
                cancellationToken);
            if (lineResult.IsTooLong)
            {
                await writer.WriteLineAsync("-ERR [SYS/PERM] command line is too long");
                session.State = Pop3State.Update;
                break;
            }
            if (lineResult.Value is null)
                break;

            var line = lineResult.Value;
            var separator = line.IndexOf(' ');
            var command = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
            var argument = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart();

            switch (command)
            {
                case "CAPA":
                    await WriteCapabilitiesAsync(writer, session);
                    break;

                case "STLS":
                    if (session.State != Pop3State.Authorization)
                    {
                        await writer.WriteLineAsync("-ERR [SYS/PERM] STLS is only valid before authentication");
                    }
                    else if (session.IsSecure)
                    {
                        await writer.WriteLineAsync("-ERR [SYS/PERM] TLS is already active");
                    }
                    else if (!environment.Pop3.EnableStartTls
                        || environment.Tls.CertificatePath is null)
                    {
                        await writer.WriteLineAsync("-ERR [SYS/PERM] STLS is not available");
                    }
                    else
                    {
                        await writer.WriteLineAsync("+OK Begin TLS negotiation");
                        await writer.FlushAsync(cancellationToken);
                        return SessionUpgrade.StartTls;
                    }
                    break;

                case "USER":
                    await HandleUserAsync(writer, argument, session);
                    break;

                case "PASS":
                    await HandlePasswordAsync(writer, argument, session, cancellationToken);
                    break;

                case "AUTH":
                    await HandleAuthAsync(
                        reader,
                        writer,
                        argument,
                        session,
                        cancellationToken);
                    break;

                case "STAT":
                    if (await RequireTransactionAsync(writer, session))
                        await WriteStatAsync(writer, session);
                    break;

                case "LIST":
                    if (await RequireTransactionAsync(writer, session))
                        await HandleListAsync(writer, argument, session);
                    break;

                case "UIDL":
                    if (await RequireTransactionAsync(writer, session))
                        await HandleUidlAsync(writer, argument, session);
                    break;

                case "RETR":
                    if (await RequireTransactionAsync(writer, session))
                    {
                        await HandleRetrieveAsync(
                            stream,
                            writer,
                            argument,
                            session,
                            bodyLineCount: null,
                            cancellationToken);
                    }
                    break;

                case "TOP":
                    if (await RequireTransactionAsync(writer, session))
                    {
                        await HandleTopAsync(
                            stream,
                            writer,
                            argument,
                            session,
                            cancellationToken);
                    }
                    break;

                case "DELE":
                    if (await RequireTransactionAsync(writer, session))
                        await HandleDeleteAsync(writer, argument, session);
                    break;

                case "RSET":
                    if (await RequireTransactionAsync(writer, session))
                    {
                        session.DeletedMessageIds.Clear();
                        await WriteStatAsync(writer, session);
                    }
                    break;

                case "NOOP":
                    if (await RequireTransactionAsync(writer, session))
                        await writer.WriteLineAsync("+OK");
                    break;

                case "QUIT":
                    await HandleQuitAsync(writer, session, cancellationToken);
                    break;

                default:
                    await writer.WriteLineAsync("-ERR [SYS/PERM] unknown command");
                    break;
            }

            if (session.AuthenticationFailures >= MaximumAuthenticationFailures
                && session.State == Pop3State.Authorization)
            {
                await writer.WriteLineAsync("-ERR [AUTH] too many authentication failures");
                session.State = Pop3State.Update;
            }
        }

        return SessionUpgrade.None;
    }

    private async Task WriteCapabilitiesAsync(StreamWriter writer, Pop3Session session)
    {
        await writer.WriteLineAsync("+OK Capability list follows");
        await writer.WriteLineAsync("TOP");
        await writer.WriteLineAsync("RESP-CODES");
        await writer.WriteLineAsync("PIPELINING");
        await writer.WriteLineAsync("UIDL");
        await writer.WriteLineAsync("EXPIRE NEVER");
        if (session.State == Pop3State.Authorization)
        {
            await writer.WriteLineAsync("USER");
            if (session.IsSecure)
                await writer.WriteLineAsync("SASL PLAIN");
            else if (environment.Pop3.EnableStartTls
                && environment.Tls.CertificatePath is not null)
                await writer.WriteLineAsync("STLS");
        }
        await writer.WriteLineAsync("IMPLEMENTATION mk8.email");
        await writer.WriteLineAsync(".");
    }

    private static async Task HandleUserAsync(
        StreamWriter writer,
        string username,
        Pop3Session session)
    {
        if (session.State != Pop3State.Authorization)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] already authenticated");
            return;
        }
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync("-ERR [AUTH] TLS is required before authentication");
            return;
        }
        if (string.IsNullOrWhiteSpace(username) || username.Length > 320)
        {
            await writer.WriteLineAsync("-ERR [AUTH] invalid username");
            return;
        }

        session.PendingUsername = username;
        await writer.WriteLineAsync("+OK user accepted");
    }

    private async Task HandlePasswordAsync(
        StreamWriter writer,
        string password,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.State != Pop3State.Authorization)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] already authenticated");
            return;
        }
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync("-ERR [AUTH] TLS is required before authentication");
            return;
        }
        if (session.PendingUsername is null || password.Length == 0)
        {
            await writer.WriteLineAsync("-ERR [AUTH] USER is required before PASS");
            return;
        }

        await AuthenticateAndOpenAsync(
            writer,
            session.PendingUsername,
            password,
            session,
            cancellationToken);
    }

    private async Task HandleAuthAsync(
        BoundedLineReader reader,
        StreamWriter writer,
        string argument,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.State != Pop3State.Authorization)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] already authenticated");
            return;
        }
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync("-ERR [AUTH] TLS is required before authentication");
            return;
        }
        if (string.IsNullOrEmpty(argument))
        {
            await writer.WriteLineAsync("+OK Supported SASL mechanisms");
            await writer.WriteLineAsync("PLAIN");
            await writer.WriteLineAsync(".");
            return;
        }

        var separator = argument.IndexOf(' ');
        var mechanism = (separator < 0 ? argument : argument[..separator]).ToUpperInvariant();
        if (mechanism != "PLAIN")
        {
            await writer.WriteLineAsync("-ERR [AUTH] unsupported SASL mechanism");
            return;
        }

        var encoded = separator < 0 ? string.Empty : argument[(separator + 1)..].Trim();
        if (encoded.Length == 0)
        {
            await writer.WriteLineAsync("+ ");
            var response = await reader.ReadLineAsync(
                MaximumAuthenticationLineCharacters,
                cancellationToken);
            if (response.IsTooLong)
            {
                await writer.WriteLineAsync("-ERR [AUTH] authentication response is too long");
                return;
            }
            encoded = response.Value ?? string.Empty;
        }
        if (encoded == "*")
        {
            await writer.WriteLineAsync("-ERR [AUTH] authentication cancelled");
            return;
        }

        string authorizationIdentity;
        string username;
        string password;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var fields = decoded.Split('\0');
            if (fields.Length != 3)
                throw new FormatException();
            authorizationIdentity = fields[0];
            username = fields[1];
            password = fields[2];
        }
        catch (FormatException)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync("-ERR [AUTH] invalid SASL response");
            return;
        }

        if (username.Length == 0
            || (authorizationIdentity.Length > 0
                && !string.Equals(
                    authorizationIdentity,
                    username,
                    StringComparison.OrdinalIgnoreCase)))
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync("-ERR [AUTH] authentication failed");
            return;
        }

        await AuthenticateAndOpenAsync(
            writer,
            username,
            password,
            session,
            cancellationToken);
    }

    private async Task AuthenticateAndOpenAsync(
        StreamWriter writer,
        string username,
        string password,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IMailAuthenticator>();
        var user = await authenticator.AuthenticateAsync(username, password, cancellationToken);
        if (user is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync("-ERR [AUTH] authentication failed");
            return;
        }
        if (!_activeMaildrops.TryAdd(user.Id, 0))
        {
            await writer.WriteLineAsync("-ERR [IN-USE] maildrop is already locked");
            return;
        }

        session.LockedUserId = user.Id;
        try
        {
            await LoadMaildropAsync(user, session, cancellationToken);
        }
        catch
        {
            ReleaseMaildrop(session);
            throw;
        }

        session.UserId = user.Id;
        session.State = Pop3State.Transaction;
        session.PendingUsername = null;
        var (count, size) = GetMaildropStatistics(session);
        await writer.WriteLineAsync($"+OK maildrop has {count} messages ({size} octets)");
    }

    private async Task LoadMaildropAsync(
        AuthenticatedMailUser user,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        var separator = user.Username.LastIndexOf('@');
        if (separator <= 0 || separator == user.Username.Length - 1)
            throw new InvalidOperationException("The authenticated POP3 account has no primary mailbox.");

        var localPart = user.Username[..separator];
        var domain = user.Username[(separator + 1)..];
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var folderId = await database.Folders
            .AsNoTracking()
            .Where(folder => folder.Inbox.OwnerId == user.Id
                && folder.Inbox.AliasForInboxId == null
                && folder.Inbox.Name == localPart
                && folder.Inbox.Address.Domain == domain
                && folder.Name == DefaultFolders.Inbox)
            .Select(folder => (Guid?)folder.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The authenticated POP3 account has no INBOX.");

        var query = database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == folderId && !email.IsDeleted)
            .OrderBy(email => email.Uid)
            .Select(email => new EmailDB
            {
                Id = email.Id,
                Sender = email.Sender,
                Recipient = email.Recipient,
                Subject = email.Subject,
                Body = email.Body,
                RawHeaders = email.RawHeaders,
                RawMessage = email.RawMessage,
                MessageId = email.MessageId,
                InReplyTo = email.InReplyTo,
                Cc = email.Cc,
                ReceivedAt = email.ReceivedAt,
                Uid = email.Uid,
            });

        await foreach (var email in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var wireMessage = BuildWireMessage(email);
            session.Messages.Add(new Pop3Message(
                session.Messages.Count + 1,
                email.Id,
                email.Uid,
                wireMessage.Length));
        }
    }

    private static async Task<bool> RequireTransactionAsync(
        StreamWriter writer,
        Pop3Session session)
    {
        if (session.State == Pop3State.Transaction)
            return true;

        await writer.WriteLineAsync("-ERR [AUTH] authenticate first");
        return false;
    }

    private static Task WriteStatAsync(StreamWriter writer, Pop3Session session)
    {
        var (count, size) = GetMaildropStatistics(session);
        return writer.WriteLineAsync($"+OK {count} {size}");
    }

    private static async Task HandleListAsync(
        StreamWriter writer,
        string argument,
        Pop3Session session)
    {
        if (argument.Length > 0)
        {
            var message = FindMessage(argument, session);
            if (message is null)
            {
                await writer.WriteLineAsync("-ERR [SYS/PERM] no such message");
                return;
            }
            await writer.WriteLineAsync($"+OK {message.Number} {message.SizeBytes}");
            return;
        }

        var available = session.Messages
            .Where(message => !session.DeletedMessageIds.Contains(message.Id))
            .ToList();
        await writer.WriteLineAsync($"+OK {available.Count} messages");
        foreach (var message in available)
            await writer.WriteLineAsync($"{message.Number} {message.SizeBytes}");
        await writer.WriteLineAsync(".");
    }

    private static async Task HandleUidlAsync(
        StreamWriter writer,
        string argument,
        Pop3Session session)
    {
        if (argument.Length > 0)
        {
            var message = FindMessage(argument, session);
            if (message is null)
            {
                await writer.WriteLineAsync("-ERR [SYS/PERM] no such message");
                return;
            }
            await writer.WriteLineAsync($"+OK {message.Number} {message.UniqueId}");
            return;
        }

        var available = session.Messages
            .Where(message => !session.DeletedMessageIds.Contains(message.Id))
            .ToList();
        await writer.WriteLineAsync($"+OK {available.Count} messages");
        foreach (var message in available)
            await writer.WriteLineAsync($"{message.Number} {message.UniqueId}");
        await writer.WriteLineAsync(".");
    }

    private async Task HandleTopAsync(
        Stream stream,
        StreamWriter writer,
        string argument,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[1], out var bodyLineCount)
            || bodyLineCount < 0)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] syntax: TOP message lines");
            return;
        }

        await HandleRetrieveAsync(
            stream,
            writer,
            parts[0],
            session,
            bodyLineCount,
            cancellationToken);
    }

    private async Task HandleRetrieveAsync(
        Stream stream,
        StreamWriter writer,
        string messageNumber,
        Pop3Session session,
        int? bodyLineCount,
        CancellationToken cancellationToken)
    {
        var message = FindMessage(messageNumber, session);
        if (message is null)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] no such message");
            return;
        }
        if (!_retrievalLimiter.Wait(0))
        {
            await writer.WriteLineAsync("-ERR [SYS/TEMP] too many concurrent retrievals");
            return;
        }

        try
        {
            var wireMessage = await GetWireMessageAsync(message.Id, session, cancellationToken);
            if (wireMessage is null)
            {
                await writer.WriteLineAsync("-ERR [SYS/TEMP] message is no longer available");
                return;
            }
            if (bodyLineCount is not null)
                wireMessage = TakeTop(wireMessage, bodyLineCount.Value);

            await writer.WriteLineAsync($"+OK {wireMessage.Length} octets");
            await writer.FlushAsync(cancellationToken);
            await WriteDotStuffedAsync(stream, wireMessage, cancellationToken);
        }
        finally
        {
            _retrievalLimiter.Release();
        }
    }

    private async Task<byte[]?> GetWireMessageAsync(
        Guid messageId,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var email = await database.Emails
            .AsNoTracking()
            .Where(candidate => candidate.Id == messageId
                && candidate.Folder.Inbox.OwnerId == session.UserId
                && !candidate.IsDeleted)
            .Select(candidate => new EmailDB
            {
                Id = candidate.Id,
                Sender = candidate.Sender,
                Recipient = candidate.Recipient,
                Subject = candidate.Subject,
                Body = candidate.Body,
                RawHeaders = candidate.RawHeaders,
                RawMessage = candidate.RawMessage,
                MessageId = candidate.MessageId,
                InReplyTo = candidate.InReplyTo,
                Cc = candidate.Cc,
                ReceivedAt = candidate.ReceivedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return email is null ? null : BuildWireMessage(email);
    }

    private static async Task HandleDeleteAsync(
        StreamWriter writer,
        string argument,
        Pop3Session session)
    {
        var message = FindMessage(argument, session);
        if (message is null)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] no such message");
            return;
        }

        session.DeletedMessageIds.Add(message.Id);
        await writer.WriteLineAsync($"+OK message {message.Number} marked for deletion");
    }

    private async Task HandleQuitAsync(
        StreamWriter writer,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.State != Pop3State.Transaction)
        {
            await writer.WriteLineAsync("+OK goodbye");
            session.State = Pop3State.Update;
            return;
        }

        try
        {
            var deletedCount = await CommitDeletesAsync(session, cancellationToken);
            await writer.WriteLineAsync($"+OK goodbye ({deletedCount} messages deleted)");
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(exception, "POP3 update failed for user {UserId}", session.UserId);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] unable to update maildrop");
        }
        finally
        {
            session.State = Pop3State.Update;
        }
    }

    private async Task<int> CommitDeletesAsync(
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.DeletedMessageIds.Count == 0)
            return 0;

        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;
        var messageIds = session.DeletedMessageIds.ToArray();
        var emails = await database.Emails
            .Where(email => messageIds.Contains(email.Id)
                && email.Folder.Inbox.OwnerId == session.UserId)
            .OrderBy(email => email.FolderId)
            .ThenBy(email => email.Uid)
            .ToListAsync(cancellationToken);
        var folderIds = emails.Select(email => email.FolderId).Distinct().ToArray();
        var folders = await database.Folders
            .Where(folder => folderIds.Contains(folder.Id))
            .ToDictionaryAsync(folder => folder.Id, cancellationToken);

        foreach (var email in emails)
        {
            var folder = folders[email.FolderId];
            database.ExpungedUids.Add(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = email.Uid,
                ModSeq = ++folder.HighestModSeq,
                FolderId = folder.Id,
            });
            database.Emails.Remove(email);
        }

        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return emails.Count;
    }

    private static Pop3Message? FindMessage(string argument, Pop3Session session)
    {
        if (!int.TryParse(argument, out var number) || number <= 0)
            return null;

        var message = session.Messages.FirstOrDefault(candidate => candidate.Number == number);
        return message is null || session.DeletedMessageIds.Contains(message.Id)
            ? null
            : message;
    }

    private static (int Count, long Size) GetMaildropStatistics(Pop3Session session)
    {
        var available = session.Messages
            .Where(message => !session.DeletedMessageIds.Contains(message.Id));
        return (available.Count(), available.Sum(message => (long)message.SizeBytes));
    }

    private static byte[] BuildWireMessage(EmailDB email)
    {
        byte[] raw;
        if (email.RawMessage is not null)
        {
            raw = email.RawMessage;
        }
        else if (email.RawHeaders is not null)
        {
            raw = MailWireEncoding.Instance.GetBytes(
                email.RawHeaders + "\r\n\r\n" + email.Body);
        }
        else
        {
            var builder = new StringBuilder();
            builder.Append($"From: {email.Sender}\r\n");
            builder.Append($"To: {email.Recipient}\r\n");
            if (!string.IsNullOrEmpty(email.Cc))
                builder.Append($"Cc: {email.Cc}\r\n");
            builder.Append($"Subject: {email.Subject}\r\n");
            builder.Append($"Date: {email.ReceivedAt:ddd, dd MMM yyyy HH:mm:ss +0000}\r\n");
            if (!string.IsNullOrEmpty(email.MessageId))
                builder.Append($"Message-ID: {email.MessageId}\r\n");
            if (!string.IsNullOrEmpty(email.InReplyTo))
                builder.Append($"In-Reply-To: {email.InReplyTo}\r\n");
            builder.Append("MIME-Version: 1.0\r\n");
            builder.Append("Content-Type: text/plain; charset=UTF-8\r\n");
            builder.Append("\r\n");
            builder.Append(email.Body);
            raw = MailWireEncoding.Instance.GetBytes(builder.ToString());
        }

        return NormalizeCrlf(raw);
    }

    private static byte[] NormalizeCrlf(ReadOnlySpan<byte> source)
    {
        using var output = new MemoryStream(source.Length + 2);
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            if (value == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                output.WriteByte((byte)'\r');
                output.WriteByte((byte)'\n');
            }
            else if (value == '\n')
            {
                output.WriteByte((byte)'\r');
                output.WriteByte((byte)'\n');
            }
            else
            {
                output.WriteByte(value);
            }
        }

        var normalized = output.ToArray();
        if (normalized.Length >= 2
            && normalized[^2] == '\r'
            && normalized[^1] == '\n')
        {
            return normalized;
        }

        Array.Resize(ref normalized, normalized.Length + 2);
        normalized[^2] = (byte)'\r';
        normalized[^1] = (byte)'\n';
        return normalized;
    }

    private static byte[] TakeTop(byte[] message, int bodyLineCount)
    {
        var bodyStart = FindHeaderBodySeparator(message);
        if (bodyStart < 0)
            return message;

        var end = bodyStart;
        for (var line = 0; line < bodyLineCount && end < message.Length; line++)
        {
            var nextLine = FindCrlf(message, end);
            end = nextLine < 0 ? message.Length : nextLine + 2;
        }
        return message[..end];
    }

    private static int FindHeaderBodySeparator(byte[] message)
    {
        for (var index = 0; index <= message.Length - 4; index++)
        {
            if (message[index] == '\r'
                && message[index + 1] == '\n'
                && message[index + 2] == '\r'
                && message[index + 3] == '\n')
            {
                return index + 4;
            }
        }
        return -1;
    }

    private static int FindCrlf(byte[] message, int start)
    {
        for (var index = start; index < message.Length - 1; index++)
        {
            if (message[index] == '\r' && message[index + 1] == '\n')
                return index;
        }
        return -1;
    }

    private static async Task WriteDotStuffedAsync(
        Stream stream,
        byte[] message,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var buffered = 0;
        var atLineStart = true;
        foreach (var value in message)
        {
            if (atLineStart && value == '.')
            {
                if (buffered == buffer.Length)
                {
                    await stream.WriteAsync(buffer, cancellationToken);
                    buffered = 0;
                }
                buffer[buffered++] = (byte)'.';
            }
            if (buffered == buffer.Length)
            {
                await stream.WriteAsync(buffer, cancellationToken);
                buffered = 0;
            }
            buffer[buffered++] = value;
            atLineStart = value == '\n';
        }
        if (buffered > 0)
            await stream.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken);
        await stream.WriteAsync(".\r\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private void RecordAuthenticationFailure(Pop3Session session)
    {
        session.AuthenticationFailures++;
        logger.LogWarning(
            "Mail authentication failed for protocol POP3 from {RemoteIp}",
            session.RemoteIp);
    }

    private void ReleaseMaildrop(Pop3Session session)
    {
        if (session.LockedUserId is not { } userId)
            return;

        _activeMaildrops.TryRemove(userId, out _);
        session.LockedUserId = null;
    }

    private X509Certificate2 LoadCertificate()
    {
        if (environment.Tls.CertificateKeyPath is not null)
        {
            return X509Certificate2.CreateFromPemFile(
                environment.Tls.CertificatePath!,
                environment.Tls.CertificateKeyPath);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(
            environment.Tls.CertificatePath!,
            password: null);
    }

    private async Task<bool> TryAuthenticateAsServerAsync(
        SslStream stream,
        X509Certificate2 certificate,
        CancellationToken cancellationToken,
        string remoteLabel)
    {
        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or AuthenticationException or SocketException)
        {
            logger.LogDebug(
                "POP3 TLS handshake from {Endpoint} ended before authentication: {ExceptionType}",
                remoteLabel,
                exception.GetType().Name);
            return false;
        }
    }
}
