using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

public sealed partial class Pop3ServerService(
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
    private readonly Pop3RetrievalLimiter _retrievalLimiter = new(4);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "No POP3 listeners are enabled.")]
    private static partial void LogNoListeners(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "POP3 {Mode} listener started on port {Port}")]
    private static partial void LogListenerStarted(ILogger logger, ListenerMode mode, int port);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "POP3 {Mode} listener on port {Port} stopped")]
    private static partial void LogListenerStopped(ILogger logger, ListenerMode mode, int port);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Rejected POP3 connection from {Endpoint}: connection limit")]
    private static partial void LogConnectionRejected(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "POP3 connection from {Endpoint} timed out")]
    private static partial void LogConnectionTimedOut(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Error handling POP3 connection from {Endpoint}")]
    private static partial void LogConnectionFailed(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "POP3 maildrop lease renewal failed for {UserId}")]
    private static partial void LogLeaseRenewalFailed(ILogger logger, Exception exception, Guid? userId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "POP3 OAuth authentication service is unavailable")]
    private static partial void LogOAuthUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "POP3 password authentication service is unavailable")]
    private static partial void LogPasswordUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "POP3 maildrop lease service is unavailable for {UserId}")]
    private static partial void LogLeaseServiceUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
        Message = "POP3 maildrop snapshot is unavailable for {UserId}")]
    private static partial void LogSnapshotUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
        Message = "POP3 message read is unavailable for {MessageId}")]
    private static partial void LogMessageUnavailable(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "POP3 update failed for user {UserId}")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception, Guid? userId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning,
        Message = "Mail authentication failed for protocol POP3 from {RemoteIp}")]
    private static partial void LogAuthenticationFailed(ILogger logger, string remoteIp);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning,
        Message = "Could not release POP3 maildrop lease for user {UserId}; it will expire")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug,
        Message = "POP3 TLS handshake from {Endpoint} ended before authentication: {ExceptionType}")]
    private static partial void LogTlsHandshakeEnded(ILogger logger, string endpoint, string exceptionType);

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
            LogNoListeners(logger);
            return;
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ListenAsync(int port, ListenerMode mode, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Any, port);
        var activeConnections = new List<Task>();
        listener.Start();
        LogListenerStarted(logger, mode, port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                activeConnections.RemoveAll(static task => task.IsCompletedSuccessfully);
                // The listener joins all retained accepted-handler tasks in its finally block.
#pragma warning disable CA2025
                activeConnections.Add(HandleConnectionAsync(client, mode, cancellationToken));
#pragma warning restore CA2025
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(activeConnections).ConfigureAwait(false);
            LogListenerStopped(logger, mode, port);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ordered wire-protocol state machine is kept together to preserve command and response sequencing.")]
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
            LogConnectionRejected(logger, remoteLabel);
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
                GatewayTrafficStream? recordedStream = null;
                SslStream? tlsStream = null;
                try
                {
                    if (journal is not null)
                    {
                        var traffic = new GatewayTrafficSession(
                            journal,
                            "pop3",
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["remoteEndpoint"] = remoteLabel,
                                ["listenerPort"] = ((client.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0)
                                    .ToString(CultureInfo.InvariantCulture),
                            });
                        // Both wrappers are disposed in reverse order in this scope's finally block.
#pragma warning disable CA2000
                        recordedStream = new GatewayTrafficStream(stream, traffic, leaveInnerOpen: true);
#pragma warning restore CA2000
                        stream = recordedStream;
                    }

                    if (mode == ListenerMode.ImplicitTls)
                    {
                        if (environment.Tls.CertificatePath is null)
                            throw new InvalidOperationException("Implicit POP3 TLS requires a certificate.");

                        using var certificate = LoadCertificate();
                        // tlsStream is disposed in the enclosing finally block on every exit.
#pragma warning disable CA2000
                        tlsStream = new SslStream(stream, leaveInnerStreamOpen: true);
#pragma warning restore CA2000
                        if (!await TryAuthenticateAsServerAsync(
                                tlsStream,
                                certificate,
                                remoteLabel,
                                timeout.Token).ConfigureAwait(false))
                            return;
                        stream = tlsStream;
                    }

                    var sendGreeting = true;
                    SessionUpgrade upgrade;
                    do
                    {
                        upgrade = await RunSessionAsync(stream, session, timeout, sendGreeting).ConfigureAwait(false);
                        sendGreeting = false;

                        if (upgrade == SessionUpgrade.StartTls)
                        {
                            // The certificate has a using lifetime across the handshake only.
#pragma warning disable CA2000
                            using var certificate = LoadCertificate();
#pragma warning restore CA2000
                            tlsStream = new SslStream(stream, leaveInnerStreamOpen: true);
                            if (!await TryAuthenticateAsServerAsync(
                                    tlsStream,
                                    certificate,
                                    remoteLabel,
                                    timeout.Token).ConfigureAwait(false))
                                return;
                            stream = tlsStream;
                            session.IsSecure = true;
                            session.PendingUsername = null;
                        }
                    } while (upgrade != SessionUpgrade.None && session.State != Pop3State.Update);
                }
                finally
                {
                    if (tlsStream is not null)
                        await tlsStream.DisposeAsync().ConfigureAwait(false);
                    if (recordedStream is not null)
                        await recordedStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogConnectionTimedOut(logger, remoteLabel);
        }
        // The accepted connection task owns its client and must absorb backend failures.
#pragma warning disable CA1031
        catch (Exception exception)
        {
#pragma warning restore CA1031
            LogConnectionFailed(logger, exception, remoteLabel);
        }
        finally
        {
            await ReleaseMaildropAsync(session).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ordered wire-protocol state machine is kept together to preserve command and response sequencing.")]
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
        var writer = new StreamWriter(
            stream,
            MailWireEncoding.Instance,
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };
        await using var writerLifetime = writer.ConfigureAwait(false);

        if (sendGreeting)
            await writer.WriteLineAsync($"+OK {environment.Smtp.Hostname} mk8.email POP3 ready").ConfigureAwait(false);

        while (!timeout.IsCancellationRequested && session.State != Pop3State.Update)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(environment.Limits.ConnectionTimeoutSeconds));
            var lineResult = await reader.ReadLineAsync(
                MaximumCommandLineCharacters,
                cancellationToken).ConfigureAwait(false);
            if (lineResult.IsTooLong)
            {
                await writer.WriteLineAsync("-ERR [SYS/PERM] command line is too long").ConfigureAwait(false);
                session.State = Pop3State.Update;
                break;
            }
            if (lineResult.Value is null)
                break;

            var line = lineResult.Value;
            var separator = line.IndexOf(' ', StringComparison.Ordinal);
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
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    LogLeaseRenewalFailed(logger, exception, session.UserId);
                    await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop lock is unavailable").ConfigureAwait(false);
                    session.State = Pop3State.Update;
                    break;
                }
                if (!renewed)
                {
                    await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop lock was lost").ConfigureAwait(false);
                    session.State = Pop3State.Update;
                    break;
                }
            }

            switch (command)
            {
                case "CAPA":
                    await WriteCapabilitiesAsync(writer, session).ConfigureAwait(false);
                    break;

                case "STLS":
                    if (session.State != Pop3State.Authorization)
                    {
                        await writer.WriteLineAsync("-ERR [SYS/PERM] STLS is only valid before authentication").ConfigureAwait(false);
                    }
                    else if (session.IsSecure)
                    {
                        await writer.WriteLineAsync("-ERR [SYS/PERM] TLS is already active").ConfigureAwait(false);
                    }
                    else if (!environment.Pop3.EnableStartTls
                        || environment.Tls.CertificatePath is null)
                    {
                        await writer.WriteLineAsync("-ERR [SYS/PERM] STLS is not available").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("+OK Begin TLS negotiation").ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        return SessionUpgrade.StartTls;
                    }
                    break;

                case "USER":
                    await HandleUserAsync(writer, argument, session).ConfigureAwait(false);
                    break;

                case "PASS":
                    await HandlePasswordAsync(writer, argument, session, cancellationToken).ConfigureAwait(false);
                    break;

                case "AUTH":
                    await HandleAuthAsync(
                        reader,
                        writer,
                        argument,
                        session,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "STAT":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                        await WriteStatAsync(writer, session).ConfigureAwait(false);
                    break;

                case "LIST":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                        await HandleListAsync(writer, argument, session).ConfigureAwait(false);
                    break;

                case "UIDL":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                        await HandleUidlAsync(writer, argument, session).ConfigureAwait(false);
                    break;

                case "RETR":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                    {
                        await HandleRetrieveAsync(
                            stream,
                            writer,
                            argument,
                            session,
                            bodyLineCount: null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case "TOP":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                    {
                        await HandleTopAsync(
                            stream,
                            writer,
                            argument,
                            session,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case "DELE":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                        await HandleDeleteAsync(writer, argument, session).ConfigureAwait(false);
                    break;

                case "RSET":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                    {
                        session.DeletedMessageIds.Clear();
                        await WriteStatAsync(writer, session).ConfigureAwait(false);
                    }
                    break;

                case "NOOP":
                    if (await RequireTransactionAsync(writer, session).ConfigureAwait(false))
                        await writer.WriteLineAsync("+OK").ConfigureAwait(false);
                    break;

                case "QUIT":
                    await HandleQuitAsync(writer, session, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await writer.WriteLineAsync("-ERR [SYS/PERM] unknown command").ConfigureAwait(false);
                    break;
            }

            if (session.AuthenticationFailures >= MaximumAuthenticationFailures
                && session.State == Pop3State.Authorization)
            {
                await writer.WriteLineAsync("-ERR [AUTH] too many authentication failures").ConfigureAwait(false);
                session.State = Pop3State.Update;
            }
        }

        return SessionUpgrade.None;
    }

    private TimeSpan MaildropLeaseLifetime =>
        TimeSpan.FromSeconds(environment.Limits.ConnectionTimeoutSeconds + 30);

    private async Task WriteCapabilitiesAsync(StreamWriter writer, Pop3Session session)
    {
        await writer.WriteLineAsync("+OK Capability list follows").ConfigureAwait(false);
        await writer.WriteLineAsync("TOP").ConfigureAwait(false);
        await writer.WriteLineAsync("RESP-CODES").ConfigureAwait(false);
        await writer.WriteLineAsync("PIPELINING").ConfigureAwait(false);
        await writer.WriteLineAsync("UIDL").ConfigureAwait(false);
        await writer.WriteLineAsync("EXPIRE NEVER").ConfigureAwait(false);
        if (session.State == Pop3State.Authorization)
        {
            await writer.WriteLineAsync("USER").ConfigureAwait(false);
            if (session.IsSecure)
            {
                var mechanisms = environment.OAuth.EnableOAuth ? "PLAIN XOAUTH2" : "PLAIN";
                await writer.WriteLineAsync($"SASL {mechanisms}").ConfigureAwait(false);
            }
            else if (environment.Pop3.EnableStartTls
                && environment.Tls.CertificatePath is not null)
                await writer.WriteLineAsync("STLS").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("IMPLEMENTATION mk8.email").ConfigureAwait(false);
        await writer.WriteLineAsync(".").ConfigureAwait(false);
    }

    private static async Task HandleUserAsync(
        StreamWriter writer,
        string username,
        Pop3Session session)
    {
        if (session.State != Pop3State.Authorization)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] already authenticated").ConfigureAwait(false);
            return;
        }
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync("-ERR [AUTH] TLS is required before authentication").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(username) || username.Length > 320)
        {
            await writer.WriteLineAsync("-ERR [AUTH] invalid username").ConfigureAwait(false);
            return;
        }

        session.PendingUsername = username;
        await writer.WriteLineAsync("+OK user accepted").ConfigureAwait(false);
    }

    private async Task HandlePasswordAsync(
        StreamWriter writer,
        string password,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.State != Pop3State.Authorization)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] already authenticated").ConfigureAwait(false);
            return;
        }
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync("-ERR [AUTH] TLS is required before authentication").ConfigureAwait(false);
            return;
        }
        if (session.PendingUsername is null || password.Length == 0)
        {
            await writer.WriteLineAsync("-ERR [AUTH] USER is required before PASS").ConfigureAwait(false);
            return;
        }

        await AuthenticateAndOpenAsync(
            writer,
            session.PendingUsername,
            password,
            session,
            cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ordered wire-protocol state machine is kept together to preserve command and response sequencing.")]
    private async Task HandleAuthAsync(
        BoundedLineReader reader,
        StreamWriter writer,
        string argument,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.State != Pop3State.Authorization)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] already authenticated").ConfigureAwait(false);
            return;
        }
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync("-ERR [AUTH] TLS is required before authentication").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrEmpty(argument))
        {
            await writer.WriteLineAsync("+OK Supported SASL mechanisms").ConfigureAwait(false);
            await writer.WriteLineAsync("PLAIN").ConfigureAwait(false);
            if (environment.OAuth.EnableOAuth)
                await writer.WriteLineAsync("XOAUTH2").ConfigureAwait(false);
            await writer.WriteLineAsync(".").ConfigureAwait(false);
            return;
        }

        var separator = argument.IndexOf(' ', StringComparison.Ordinal);
        var mechanism = (separator < 0 ? argument : argument[..separator]).ToUpperInvariant();
        if (mechanism is not ("PLAIN" or "XOAUTH2")
            || string.Equals(mechanism, "XOAUTH2", StringComparison.Ordinal) && !environment.OAuth.EnableOAuth)
        {
            await writer.WriteLineAsync("-ERR [AUTH] unsupported SASL mechanism").ConfigureAwait(false);
            return;
        }

        var encoded = separator < 0 ? string.Empty : argument[(separator + 1)..].Trim();
        if (encoded.Length == 0)
        {
            await writer.WriteLineAsync("+ ").ConfigureAwait(false);
            var response = await reader.ReadLineAsync(
                MaximumAuthenticationLineCharacters,
                cancellationToken).ConfigureAwait(false);
            if (response.IsTooLong)
            {
                await writer.WriteLineAsync("-ERR [AUTH] authentication response is too long").ConfigureAwait(false);
                return;
            }
            encoded = response.Value ?? string.Empty;
        }
        if (string.Equals(encoded, "*", StringComparison.Ordinal))
        {
            await writer.WriteLineAsync("-ERR [AUTH] authentication cancelled").ConfigureAwait(false);
            return;
        }

        if (string.Equals(mechanism, "XOAUTH2", StringComparison.Ordinal))
        {
            if (!OAuthSasl.TryParseXOAuth2(encoded, out var oauthUsername, out var accessToken))
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync("-ERR [AUTH] authentication failed").ConfigureAwait(false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
            Pop3IdentityResult oauthUser;
            try
            {
                oauthUser = await application.AuthenticateOAuthAsync(
                    new Pop3OAuthAuthentication(oauthUsername, accessToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LogOAuthUnavailable(logger, exception);
                await writer.WriteLineAsync("-ERR [SYS/TEMP] authentication service is unavailable").ConfigureAwait(false);
                return;
            }
            if (oauthUser.UserId is null || oauthUser.Username is null)
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync("-ERR [AUTH] authentication failed").ConfigureAwait(false);
                return;
            }

            await OpenMaildropAsync(writer, oauthUser, session, cancellationToken).ConfigureAwait(false);
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
            await writer.WriteLineAsync("-ERR [AUTH] invalid SASL response").ConfigureAwait(false);
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
            await writer.WriteLineAsync("-ERR [AUTH] authentication failed").ConfigureAwait(false);
            return;
        }

        await AuthenticateAndOpenAsync(
            writer,
            username,
            password,
            session,
            cancellationToken).ConfigureAwait(false);
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
                new Pop3PasswordAuthentication(username, password), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogPasswordUnavailable(logger, exception);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] authentication service is unavailable").ConfigureAwait(false);
            return;
        }
        if (user.UserId is null || user.Username is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync("-ERR [AUTH] authentication failed").ConfigureAwait(false);
            return;
        }

        await OpenMaildropAsync(writer, user, session, cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogLeaseServiceUnavailable(logger, exception, userId);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop is temporarily unavailable").ConfigureAwait(false);
            return;
        }
        if (maildropLease is null)
        {
            await writer.WriteLineAsync("-ERR [IN-USE] maildrop is already locked").ConfigureAwait(false);
            return;
        }

        session.MaildropLease = maildropLease;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IPop3ApplicationService>();
            var snapshot = await application.ListMaildropAsync(
                new Pop3UserRequest(userId), cancellationToken).ConfigureAwait(false);
            session.Messages.Clear();
            foreach (ref readonly var message in CollectionsMarshal.AsSpan(snapshot.Messages))
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
            await ReleaseMaildropAsync(session).ConfigureAwait(false);
            LogSnapshotUnavailable(logger, exception, userId);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] maildrop is temporarily unavailable").ConfigureAwait(false);
            return;
        }
        catch
        {
            await ReleaseMaildropAsync(session).ConfigureAwait(false);
            throw;
        }

        session.UserId = userId;
        session.State = Pop3State.Transaction;
        session.PendingUsername = null;
        var (count, size) = GetMaildropStatistics(session);
        await writer.WriteLineAsync($"+OK maildrop has {count} messages ({size} octets)").ConfigureAwait(false);
    }

    private static async Task<bool> RequireTransactionAsync(
        StreamWriter writer,
        Pop3Session session)
    {
        if (session.State == Pop3State.Transaction)
            return true;

        await writer.WriteLineAsync("-ERR [AUTH] authenticate first").ConfigureAwait(false);
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
                await writer.WriteLineAsync("-ERR [SYS/PERM] no such message").ConfigureAwait(false);
                return;
            }
            await writer.WriteLineAsync($"+OK {message.Number} {message.SizeBytes}").ConfigureAwait(false);
            return;
        }

        var available = session.Messages
            .Where(message => !session.DeletedMessageIds.Contains(message.Id))
            .ToList();
        await writer.WriteLineAsync($"+OK {available.Count} messages").ConfigureAwait(false);
        // Each awaited protocol write can suspend; a Span enumerator cannot cross that boundary.
#pragma warning disable HLQ012
        foreach (var message in available)
            await writer.WriteLineAsync($"{message.Number} {message.SizeBytes}").ConfigureAwait(false);
#pragma warning restore HLQ012
        await writer.WriteLineAsync(".").ConfigureAwait(false);
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
                await writer.WriteLineAsync("-ERR [SYS/PERM] no such message").ConfigureAwait(false);
                return;
            }
            await writer.WriteLineAsync($"+OK {message.Number} {message.UniqueId}").ConfigureAwait(false);
            return;
        }

        var available = session.Messages
            .Where(message => !session.DeletedMessageIds.Contains(message.Id))
            .ToList();
        await writer.WriteLineAsync($"+OK {available.Count} messages").ConfigureAwait(false);
        // Each awaited protocol write can suspend; a Span enumerator cannot cross that boundary.
#pragma warning disable HLQ012
        foreach (var message in available)
            await writer.WriteLineAsync($"{message.Number} {message.UniqueId}").ConfigureAwait(false);
#pragma warning restore HLQ012
        await writer.WriteLineAsync(".").ConfigureAwait(false);
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
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyLineCount)
            || bodyLineCount < 0)
        {
            await writer.WriteLineAsync("-ERR [SYS/PERM] syntax: TOP message lines").ConfigureAwait(false);
            return;
        }

        await HandleRetrieveAsync(
            stream,
            writer,
            parts[0],
            session,
            bodyLineCount,
            cancellationToken).ConfigureAwait(false);
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
            await writer.WriteLineAsync("-ERR [SYS/PERM] no such message").ConfigureAwait(false);
            return;
        }
        using var retrievalSlot = _retrievalLimiter.TryAcquire();
        if (retrievalSlot is null)
        {
            await writer.WriteLineAsync("-ERR [SYS/TEMP] too many concurrent retrievals").ConfigureAwait(false);
            return;
        }

        {
            byte[]? wireMessage;
            try
            {
                wireMessage = await GetWireMessageAsync(message.Id, session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LogMessageUnavailable(logger, exception, message.Id);
                await writer.WriteLineAsync("-ERR [SYS/TEMP] message is temporarily unavailable").ConfigureAwait(false);
                return;
            }
            if (wireMessage is null)
            {
                await writer.WriteLineAsync("-ERR [SYS/TEMP] message is no longer available").ConfigureAwait(false);
                return;
            }
            if (bodyLineCount is not null)
                wireMessage = Pop3WireCodec.TakeTop(wireMessage, bodyLineCount.Value);

            await writer.WriteLineAsync($"+OK {wireMessage.Length} octets").ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Pop3WireCodec.WriteDotStuffedAsync(stream, wireMessage, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
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
            await writer.WriteLineAsync("-ERR [SYS/PERM] no such message").ConfigureAwait(false);
            return;
        }

        session.DeletedMessageIds.Add(message.Id);
        await writer.WriteLineAsync($"+OK message {message.Number} marked for deletion").ConfigureAwait(false);
    }

    private async Task HandleQuitAsync(
        StreamWriter writer,
        Pop3Session session,
        CancellationToken cancellationToken)
    {
        if (session.State != Pop3State.Transaction)
        {
            await writer.WriteLineAsync("+OK goodbye").ConfigureAwait(false);
            session.State = Pop3State.Update;
            return;
        }

        try
        {
            var deletedCount = await CommitDeletesAsync(session, cancellationToken).ConfigureAwait(false);
            await ReleaseMaildropAsync(session).ConfigureAwait(false);
            await writer.WriteLineAsync($"+OK goodbye ({deletedCount} messages deleted)").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUpdateFailed(logger, exception, session.UserId);
            await ReleaseMaildropAsync(session).ConfigureAwait(false);
            await writer.WriteLineAsync("-ERR [SYS/TEMP] unable to update maildrop").ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
        return result.DeletedCount;
    }

    private static Pop3Message? FindMessage(string argument, Pop3Session session)
    {
        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
            return null;

        var message = session.Messages.FirstOrDefault(candidate => candidate.Number == number);
        return message is null || session.DeletedMessageIds.Contains(message.Id)
            ? null
            : message;
    }

    private static (int Count, long Size) GetMaildropStatistics(Pop3Session session)
    {
        var count = 0;
        long size = 0;
        foreach (ref readonly var message in CollectionsMarshal.AsSpan(session.Messages))
        {
            if (session.DeletedMessageIds.Contains(message.Id))
                continue;
            count++;
            size += message.SizeBytes;
        }
        return (count, size);
    }

    private void RecordAuthenticationFailure(Pop3Session session)
    {
        session.AuthenticationFailures++;
        LogAuthenticationFailed(logger, session.RemoteIp);
    }

    private async Task ReleaseMaildropAsync(Pop3Session session)
    {
        if (session.MaildropLease is not { } lease)
            return;
        session.MaildropLease = null;
        try
        {
            await leaseStore.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        // The lease expires independently; release failure cannot conceal the session result.
#pragma warning disable CA1031
        catch (Exception exception)
        {
#pragma warning restore CA1031
            LogReleaseFailed(logger, exception, lease.UserId);
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
        string remoteLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    // Mail listeners require TLS 1.2 or later even if the host enables legacy protocols.
#pragma warning disable CA5398
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or AuthenticationException or SocketException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                // The expensive exception type name is evaluated only after the level check.
#pragma warning disable CA1873
                LogTlsHandshakeEnded(logger, remoteLabel, exception.GetType().Name);
#pragma warning restore CA1873
            }
            return false;
        }
    }
}
