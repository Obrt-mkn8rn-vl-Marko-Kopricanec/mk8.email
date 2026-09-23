using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Contracts.Pop3;
using mk8.email.MailWire;
using mk8.email.Messaging;

namespace mk8.email.Gateway.Protocols.Pop3;

public sealed class Pop3ServerService(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig environment,
    ILogger<Pop3ServerService> logger,
    IPop3MaildropLeaseStore leaseStore,
    IGatewayTrafficJournal? journal = null) : BackgroundService
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
        public Pop3MaildropLease? MaildropLease { get; set; }
        public int AuthenticationFailures { get; set; }
        public required string RemoteIp { get; init; }
        public List<Pop3Message> Messages { get; } = [];
        public HashSet<Guid> DeletedMessageIds { get; } = [];
    }

    private readonly ConnectionLimiter _connectionLimiter = new(MaximumConcurrentConnections);
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
                if (journal is not null)
                {
                    var traffic = new GatewayTrafficSession(
                        journal,
                        "pop3",
                        new Dictionary<string, string>
                        {
                            ["remoteEndpoint"] = remoteLabel,
                            ["listenerPort"] = ((client.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0)
                                .ToString(CultureInfo.InvariantCulture),
                        });
                    stream = new GatewayTrafficStream(stream, traffic, leaveInnerOpen: false);
                }

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
            await ReleaseMaildropAsync(session);
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

            if (session.MaildropLease is { } maildropLease)
            {
                bool renewed;
                try
                {
                    renewed = await leaseStore.RenewAsync(
                        maildropLease,
                        MaildropLeaseLifetime,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "POP3 maildrop lease renewal failed for {UserId}", session.UserId);
                    await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop lock is unavailable");
                    session.State = Pop3State.Update;
                    break;
                }
                if (!renewed)
                {
                    await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop lock was lost");
                    session.State = Pop3State.Update;
                    break;
                }
            }

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

    private TimeSpan MaildropLeaseLifetime =>
        TimeSpan.FromSeconds(environment.Limits.ConnectionTimeoutSeconds + 30);

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
            {
                var mechanisms = environment.OAuth.EnableOAuth ? "PLAIN XOAUTH2" : "PLAIN";
                await writer.WriteLineAsync($"SASL {mechanisms}");
            }
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
            if (environment.OAuth.EnableOAuth)
                await writer.WriteLineAsync("XOAUTH2");
            await writer.WriteLineAsync(".");
            return;
        }

        var separator = argument.IndexOf(' ');
        var mechanism = (separator < 0 ? argument : argument[..separator]).ToUpperInvariant();
        if (mechanism is not ("PLAIN" or "XOAUTH2")
            || mechanism == "XOAUTH2" && !environment.OAuth.EnableOAuth)
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

        if (mechanism == "XOAUTH2")
        {
            if (!OAuthSasl.TryParseXOAuth2(encoded, out var oauthUsername, out var accessToken))
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync("-ERR [AUTH] authentication failed");
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
            Pop3IdentityResult oauthUser;
            try
            {
                oauthUser = await application.AuthenticateOAuthAsync(
                    new Pop3OAuthAuthentication(oauthUsername, accessToken), cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "POP3 OAuth authentication service is unavailable");
                await writer.WriteLineAsync("-ERR [SYS/TEMP] authentication service is unavailable");
                return;
            }
            if (oauthUser.UserId is null || oauthUser.Username is null)
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync("-ERR [AUTH] authentication failed");
                return;
            }

            await OpenMaildropAsync(writer, oauthUser, session, cancellationToken);
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
        var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
        Pop3IdentityResult user;
        try
        {
            user = await application.AuthenticatePasswordAsync(
                new Pop3PasswordAuthentication(username, password), cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "POP3 password authentication service is unavailable");
            await writer.WriteLineAsync("-ERR [SYS/TEMP] authentication service is unavailable");
            return;
        }
        if (user.UserId is null || user.Username is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync("-ERR [AUTH] authentication failed");
            return;
        }

        await OpenMaildropAsync(writer, user, session, cancellationToken);
    }

    private async Task OpenMaildropAsync(
        StreamWriter writer,
        Pop3IdentityResult user,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        var userId = user.UserId
            ?? throw new InvalidOperationException("The authenticated POP3 identity is missing.");
        Pop3MaildropLease? maildropLease;
        try
        {
            maildropLease = await leaseStore.TryAcquireAsync(
                userId,
                MaildropLeaseLifetime,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "POP3 maildrop lease service is unavailable for {UserId}", userId);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop is temporarily unavailable");
            return;
        }
        if (maildropLease is null)
        {
            await writer.WriteLineAsync("-ERR [IN-USE] maildrop is already locked");
            return;
        }

        session.MaildropLease = maildropLease;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
            var snapshot = await application.ListMaildropAsync(
                new Pop3UserRequest(userId), cancellationToken);
            session.Messages.Clear();
            foreach (var message in snapshot.Messages)
            {
                session.Messages.Add(new Pop3Message(
                    session.Messages.Count + 1,
                    message.Id,
                    message.Uid,
                    message.SizeBytes));
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await ReleaseMaildropAsync(session);
            logger.LogWarning(exception, "POP3 maildrop snapshot is unavailable for {UserId}", userId);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop is temporarily unavailable");
            return;
        }
        catch
        {
            await ReleaseMaildropAsync(session);
            throw;
        }

        session.UserId = userId;
        session.State = Pop3State.Transaction;
        session.PendingUsername = null;
        var (count, size) = GetMaildropStatistics(session);
        await writer.WriteLineAsync($"+OK maildrop has {count} messages ({size} octets)");
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
            byte[]? wireMessage;
            try
            {
                wireMessage = await GetWireMessageAsync(message.Id, session, cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "POP3 message read is unavailable for {MessageId}", message.Id);
                await writer.WriteLineAsync("-ERR [SYS/TEMP] message is temporarily unavailable");
                return;
            }
            if (wireMessage is null)
            {
                await writer.WriteLineAsync("-ERR [SYS/TEMP] message is no longer available");
                return;
            }
            if (bodyLineCount is not null)
                wireMessage = Pop3WireCodec.TakeTop(wireMessage, bodyLineCount.Value);

            await writer.WriteLineAsync($"+OK {wireMessage.Length} octets");
            await writer.FlushAsync(cancellationToken);
            await Pop3WireCodec.WriteDotStuffedAsync(stream, wireMessage, cancellationToken);
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
        var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
        var result = await application.GetMessageAsync(
            new Pop3MessageRequest(
                session.UserId ?? throw new InvalidOperationException("The POP3 session is not authenticated."),
                messageId),
            cancellationToken);
        return result.RawMessage is null
            ? null
            : Pop3WireCodec.NormalizeCrlf(result.RawMessage);
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
            await ReleaseMaildropAsync(session);
            await writer.WriteLineAsync($"+OK goodbye ({deletedCount} messages deleted)");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "POP3 update failed for user {UserId}", session.UserId);
            await ReleaseMaildropAsync(session);
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
        var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
        var result = await application.CommitDeletesAsync(
            new Pop3DeleteRequest(
                session.UserId ?? throw new InvalidOperationException("The POP3 session is not authenticated."),
                session.DeletedMessageIds.ToArray()),
            cancellationToken);
        return result.DeletedCount;
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

    private void RecordAuthenticationFailure(Pop3Session session)
    {
        session.AuthenticationFailures++;
        logger.LogWarning(
            "Mail authentication failed for protocol POP3 from {RemoteIp}",
            session.RemoteIp);
    }

    private async Task ReleaseMaildropAsync(Pop3Session session)
    {
        if (session.MaildropLease is not { } lease)
            return;
        session.MaildropLease = null;
        try
        {
            await leaseStore.ReleaseAsync(lease, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not release POP3 maildrop lease for user {UserId}; it will expire",
                lease.UserId);
        }
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
