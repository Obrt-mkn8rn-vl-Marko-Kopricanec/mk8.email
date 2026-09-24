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
using mk8.email.Contracts.Mail;
using mk8.email.MailWire;
using mk8.email.Messaging;

namespace mk8.email.Smtp.Presentation;

public partial class SmtpServerService(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig env,
    ILogger<SmtpServerService> logger,
    IGatewayTrafficJournal? journal = null) : BackgroundService
{
    private const int MaximumCommandLineCharacters = 4096;
    private const int MaximumDataLineCharacters = 998;
    private const int MaximumConcurrentConnections = 256;
    private const int MaximumConcurrentDataTransactions = 4;
    private static readonly Encoding ProtocolEncoding = MailWireEncoding.Instance;

    private enum ListenerMode { Smtp, Submission, ImplicitTls }

    private sealed class SmtpSession
    {
        public required ListenerMode Mode { get; init; }
        public bool IsSecure { get; set; }
        public string? Helo { get; set; }
        public bool HasGreeting => Helo is not null;
        public bool IsExtendedSmtp { get; set; }
        public string? AuthenticatedUser { get; set; }
        public bool IsAuthenticated => AuthenticatedUser is not null;
        public string? Sender { get; set; }
        public bool HasMailFrom { get; set; }
        public List<MailEnvelopeRecipient> Recipients { get; } = [];
        public StringBuilder DataBuilder { get; } = new();
        public int DataByteCount { get; set; }
        public bool MessageTooLarge { get; set; }
        public string? DataFailureResponse { get; set; }
        public bool SmtpUtf8 { get; set; }
        public bool BodyIsEightBit { get; set; }
        public string? DsnReturnContent { get; set; }
        public string? DsnEnvelopeId { get; set; }
        public bool InDataMode { get; set; }
        public int AuthenticationFailures { get; set; }
        private IDisposable? DataLease { get; set; }

        public bool TryEnterDataMode(ConnectionLimiter dataLimiter)
        {
            var lease = dataLimiter.TryAcquire(IPAddress.None, MaximumConcurrentDataTransactions);
            if (lease is null)
                return false;

            DataLease = lease;
            InDataMode = true;
            return true;
        }

        public void Reset()
        {
            var dataLease = DataLease;
            DataLease = null;
            dataLease?.Dispose();
            Sender = null;
            HasMailFrom = false;
            Recipients.Clear();
            DataBuilder.Clear();
            if (DataBuilder.Capacity > 4096)
                DataBuilder.Capacity = 4096;
            DataByteCount = 0;
            MessageTooLarge = false;
            DataFailureResponse = null;
            SmtpUtf8 = false;
            BodyIsEightBit = false;
            DsnReturnContent = null;
            DsnEnvelopeId = null;
            InDataMode = false;
        }
    }

    private readonly ConnectionLimiter _connectionLimiter = new(MaximumConcurrentConnections);
    private readonly ConnectionLimiter _dataTransactionLimiter = new(MaximumConcurrentDataTransactions);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = SmtpListenerOptions.FromEnvironment(env);

        var tasks = new List<Task>();

        if (config.EnableSmtp)
            tasks.Add(ListenAsync(config.SmtpPort, ListenerMode.Smtp, config, stoppingToken));

        if (config.EnableSubmission)
            tasks.Add(ListenAsync(config.SmtpSubmissionPort, ListenerMode.Submission, config, stoppingToken));

        if (config.EnableImplicitTls)
            tasks.Add(ListenAsync(config.SmtpImplicitTlsPort, ListenerMode.ImplicitTls, config, stoppingToken));

        if (tasks.Count == 0)
        {
            LogNoListeners(logger);
            return;
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ListenAsync(int port, ListenerMode mode, SmtpListenerOptions config, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
            LogListenerStarted(logger, mode, port);
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                // The detached handler owns and closes this client; the listener never disposes it.
#pragma warning disable CA2025
                _ = HandleConnectionAsync(client, mode, config, ct);
#pragma warning restore CA2025
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            listener.Dispose();
            LogListenerStopped(logger, mode, port);
        }
    }

    // Connection setup keeps its TLS, journal, and client-disposal lifetimes together.
#pragma warning disable MA0051
    private async Task HandleConnectionAsync(TcpClient client, ListenerMode mode, SmtpListenerOptions config, CancellationToken ct)
    {
#pragma warning restore MA0051
        using var clientLifetime = client;
        var remoteLabel = "unknown";
        try
        {
            var remoteEndpoint = client.Client.RemoteEndPoint;
            var remoteIp = (remoteEndpoint as IPEndPoint)?.Address ?? IPAddress.None;
            remoteLabel = remoteEndpoint?.ToString() ?? "unknown";
            using var connectionLease = _connectionLimiter.TryAcquire(
                remoteIp,
                config.MaxConnectionsPerIp);
            if (connectionLease is null)
            {
                LogConnectionLimit(logger, remoteLabel);
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));

            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<ISmtpApplicationService>();

            Stream stream = client.GetStream();
            if (journal is not null)
            {
                var traffic = new GatewayTrafficSession(
                    journal,
                    SmtpPresentationOperations.Protocol,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["remoteEndpoint"] = remoteLabel,
                        ["listenerPort"] = ((client.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0)
                            .ToString(CultureInfo.InvariantCulture),
                    });
                stream = new GatewayTrafficStream(stream, traffic, leaveInnerOpen: false);
            }

            SslStream? implicitTlsStream = null;
            try
            {
                if (mode == ListenerMode.ImplicitTls)
                {
                    if (config.TlsCertificatePath is null)
                        throw new InvalidOperationException("Implicit TLS requires a certificate.");

                    using var cert = LoadCertificate(config);
                    // The enclosing finally performs asynchronous disposal on every path.
#pragma warning disable CA2000
                    implicitTlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
#pragma warning restore CA2000
                    if (!await TryAuthenticateAsServerAsync(
                            implicitTlsStream,
                            cert,
                            remoteLabel,
                            timeout.Token).ConfigureAwait(false))
                    {
                        return;
                    }
                    stream = implicitTlsStream;
                }

                using var streamReader = new StreamReader(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                var reader = new BoundedLineReader(streamReader);
                var writer = new StreamWriter(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                await using var writerLifetime = writer.ConfigureAwait(false);

                var session = new SmtpSession
                {
                    Mode = mode,
                    IsSecure = mode == ListenerMode.ImplicitTls,
                };
                try
                {
                    await writer.WriteLineAsync($"220 {config.SmtpHostname} ESMTP mk8.email").ConfigureAwait(false);

                    await RunSmtpSessionAsync(
                        reader,
                        writer,
                        session,
                        application,
                        config,
                        timeout,
                        stream,
                        remoteIp.ToString()).ConfigureAwait(false);
                }
                finally
                {
                    session.Reset();
                }
            }
            finally
            {
                if (implicitTlsStream is not null)
                    await implicitTlsStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            LogConnectionTimedOut(logger, remoteLabel);
        }
        // A detached connection task must log and close rather than fault unobserved.
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogConnectionError(logger, ex, remoteLabel);
        }
    }

    // SMTP transaction state is intentionally processed as one ordered protocol state machine.
#pragma warning disable MA0051
    private async Task RunSmtpSessionAsync(
        BoundedLineReader reader, StreamWriter writer, SmtpSession session,
        ISmtpApplicationService application,
        SmtpListenerOptions config,
        CancellationTokenSource timeout, Stream? upgradableStream = null, string? clientIp = null)
    {
#pragma warning restore MA0051

        while (!timeout.IsCancellationRequested)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));
            var maximumLineLength = session.InDataMode
                ? Math.Min(config.MaxMessageSizeBytes, MaximumDataLineCharacters)
                : MaximumCommandLineCharacters;
            var readResult = await reader.ReadLineAsync(maximumLineLength, timeout.Token).ConfigureAwait(false);
            if (readResult.Value is null && !readResult.IsTooLong)
                break;

            if (readResult.IsTooLong)
            {
                if (session.InDataMode)
                {
                    session.DataFailureResponse ??= "554 5.6.0 Message line exceeds 998 octets";
                    session.DataBuilder.Clear();
                }
                else
                {
                    await writer.WriteLineAsync("500 5.5.2 Line too long").ConfigureAwait(false);
                }

                continue;
            }

            var wireLine = readResult.Value!;

            if (session.InDataMode)
            {
                if (string.Equals(wireLine, ".", StringComparison.Ordinal))
                {
                    session.InDataMode = false;
                    if (session.MessageTooLarge)
                    {
                        session.Reset();
                        await writer.WriteLineAsync("552 5.3.4 Message exceeds server limits").ConfigureAwait(false);
                    }
                    else if (session.DataFailureResponse is not null)
                    {
                        var failureResponse = session.DataFailureResponse;
                        session.Reset();
                        await writer.WriteLineAsync(failureResponse).ConfigureAwait(false);
                    }
                    else
                    {
                        var raw = session.DataBuilder.ToString();

                        if (SmtpInternationalization.HeadersRequireSmtpUtf8(raw)
                            && !session.SmtpUtf8)
                        {
                            session.Reset();
                            await writer.WriteLineAsync(
                                "554 5.6.9 UTF-8 header message requires SMTPUTF8").ConfigureAwait(false);
                            continue;
                        }
                        if (session.SmtpUtf8
                            && !SmtpInternationalization.HasValidUtf8Headers(raw))
                        {
                            session.Reset();
                            await writer.WriteLineAsync(
                                "554 5.6.0 Internationalized headers are not valid UTF-8").ConfigureAwait(false);
                            continue;
                        }
                        if (SmtpInternationalization.ContainsEightBit(raw)
                            && !session.BodyIsEightBit)
                        {
                            session.Reset();
                            await writer.WriteLineAsync(
                                "554 5.6.3 Eight-bit content requires BODY=8BITMIME").ConfigureAwait(false);
                            continue;
                        }

                        var authorizedSender = true;
                        if (session.IsAuthenticated)
                        {
                            try
                            {
                                authorizedSender = await application.CanSendAsAsync(
                                        new SmtpSenderAuthorization(
                                            session.AuthenticatedUser!, session.Sender ?? string.Empty),
                                        timeout.Token).ConfigureAwait(false)
                                    && await application.HasMatchingFromAddressAsync(
                                        new SmtpFromAddressCheck(raw, session.Sender ?? string.Empty),
                                        timeout.Token).ConfigureAwait(false);
                            }
                            catch (Exception exception) when (!timeout.IsCancellationRequested)
                            {
                                LogSenderPolicyUnavailable(logger, exception);
                                session.Reset();
                                await writer.WriteLineAsync("451 4.3.0 Sender policy is temporarily unavailable").ConfigureAwait(false);
                                continue;
                            }
                        }
                        if (!authorizedSender)
                        {
                            session.Reset();
                            await writer.WriteLineAsync("550 5.7.1 Sender identity is not authorized").ConfigureAwait(false);
                            continue;
                        }

                        var queueId = Guid.CreateVersion7();
                        var receivedMessage = BuildReceivedHeader(
                            queueId,
                            config.SmtpHostname,
                            session.Helo,
                            clientIp,
                            session.IsSecure,
                            session.IsAuthenticated,
                            session.IsExtendedSmtp,
                            session.SmtpUtf8) + raw;
                        try
                        {
                            await application.EnqueueAsync(
                                new MailSubmission(
                                    queueId,
                                    session.Sender ?? string.Empty,
                                    session.Recipients.ToList(),
                                    receivedMessage,
                                    clientIp,
                                    session.Helo,
                                    session.AuthenticatedUser,
                                    session.SmtpUtf8,
                                    new MailDsnEnvelope(
                                        session.DsnReturnContent,
                                        session.DsnEnvelopeId)),
                                timeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                        {
                            throw;
                        }
                        // Any durable queue failure must be reported as a temporary SMTP failure.
#pragma warning disable CA1031
                        catch (Exception exception)
#pragma warning restore CA1031
                        {
                            LogQueuePersistFailed(logger, exception, queueId);
                            session.Reset();
                            await writer.WriteLineAsync("451 4.3.0 Queue storage is temporarily unavailable").ConfigureAwait(false);
                            continue;
                        }

                        LogQueueAccepted(logger, queueId, session.Recipients.Count);
                        session.Reset();
                        await writer.WriteLineAsync($"250 2.0.0 Queued as {queueId:N}").ConfigureAwait(false);
                    }
                }
                else
                {
                    var messageLine = wireLine.StartsWith("..", StringComparison.Ordinal)
                        ? wireLine[1..]
                        : wireLine;
                    var lineByteCount = MailWireEncoding.Instance.GetByteCount(messageLine) + 2;

                    if (messageLine.Contains('\0', StringComparison.Ordinal))
                    {
                        session.DataFailureResponse ??= "554 5.6.0 NUL bytes are not supported";
                        session.DataBuilder.Clear();
                        continue;
                    }

                    if (!session.MessageTooLarge && session.DataFailureResponse is null)
                    {
                        if (lineByteCount > config.MaxMessageSizeBytes - session.DataByteCount)
                        {
                            session.MessageTooLarge = true;
                            session.DataBuilder.Clear();
                        }
                        else
                        {
                            session.DataBuilder.Append(messageLine).Append("\r\n");
                            session.DataByteCount += lineByteCount;
                        }
                    }
                }
                continue;
            }

            if (!SmtpInternationalization.TryDecodeCommandLine(wireLine, out var line))
            {
                await writer.WriteLineAsync("500 5.5.2 Command line is not valid UTF-8").ConfigureAwait(false);
                continue;
            }

            var spaceIdx = line.IndexOf(' ', StringComparison.Ordinal);
            var verb = (spaceIdx > 0 ? line[..spaceIdx] : line).ToUpperInvariant();

            switch (verb)
            {
                case "EHLO":
                    if (!TryGetGreeting(line, out var ehlo))
                    {
                        await writer.WriteLineAsync("501 5.5.4 A valid EHLO argument is required").ConfigureAwait(false);
                        break;
                    }
                    session.Reset();
                    session.Helo = ehlo;
                    session.IsExtendedSmtp = true;
                    await WriteEhloAsync(writer, config, session.IsSecure).ConfigureAwait(false);
                    break;

                case "HELO":
                    if (!TryGetGreeting(line, out var helo))
                    {
                        await writer.WriteLineAsync("501 5.5.4 A valid HELO argument is required").ConfigureAwait(false);
                        break;
                    }
                    session.Reset();
                    session.Helo = helo;
                    session.IsExtendedSmtp = false;
                    await writer.WriteLineAsync($"250 {config.SmtpHostname}").ConfigureAwait(false);
                    break;

                case "AUTH":
                    if (!session.HasGreeting)
                    {
                        await writer.WriteLineAsync("503 5.5.1 Send EHLO first").ConfigureAwait(false);
                        break;
                    }
                    if (!session.IsSecure)
                    {
                        await writer.WriteLineAsync("538 5.7.11 Encryption required for authentication").ConfigureAwait(false);
                        break;
                    }
                    if (session.Sender is not null)
                    {
                        await writer.WriteLineAsync("503 5.5.1 Mail transaction is already active").ConfigureAwait(false);
                        break;
                    }
                    await HandleAuthAsync(
                        line,
                        reader,
                        writer,
                        session,
                        application,
                        clientIp ?? "unknown",
                        timeout.Token).ConfigureAwait(false);
                    if (session.AuthenticationFailures >= 5)
                    {
                        await writer.WriteLineAsync("421 4.7.0 Too many authentication failures").ConfigureAwait(false);
                        return;
                    }
                    break;

                case "MAIL":
                    if (!session.HasGreeting)
                    {
                        await writer.WriteLineAsync("503 5.5.1 Send EHLO or HELO first").ConfigureAwait(false);
                        break;
                    }
                    if (!session.IsSecure && (session.Mode == ListenerMode.Submission || config.RequireTls))
                    {
                        await writer.WriteLineAsync("530 5.7.0 Issue STARTTLS first").ConfigureAwait(false);
                        break;
                    }
                    if (session.Mode is ListenerMode.Submission && !session.IsAuthenticated && config.RequireAuth)
                    {
                        await writer.WriteLineAsync("530 5.7.0 Authentication required").ConfigureAwait(false);
                        break;
                    }
                    session.Reset();
                    if (!TryParseMailCommand(
                            line,
                            session.IsExtendedSmtp,
                            allowEmpty: !session.IsAuthenticated,
                            config.MaxMessageSizeBytes,
                            out var mailCommand,
                            out var mailFailure))
                    {
                        await writer.WriteLineAsync(mailFailure).ConfigureAwait(false);
                        break;
                    }
                    var canSendAs = true;
                    if (session.IsAuthenticated)
                    {
                        try
                        {
                            canSendAs = await application.CanSendAsAsync(
                                new SmtpSenderAuthorization(
                                    session.AuthenticatedUser!, mailCommand.Address),
                                timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (!timeout.IsCancellationRequested)
                        {
                            LogSenderPolicyUnavailable(logger, exception);
                            await writer.WriteLineAsync("451 4.3.0 Sender policy is temporarily unavailable").ConfigureAwait(false);
                            break;
                        }
                    }
                    if (!canSendAs)
                    {
                        await writer.WriteLineAsync("553 5.7.1 Sender address is not authorized").ConfigureAwait(false);
                        break;
                    }
                    session.Sender = mailCommand.Address;
                    session.HasMailFrom = true;
                    session.SmtpUtf8 = mailCommand.SmtpUtf8;
                    session.BodyIsEightBit = mailCommand.BodyIsEightBit;
                    session.DsnReturnContent = mailCommand.DsnReturnContent;
                    session.DsnEnvelopeId = mailCommand.DsnEnvelopeId;
                    await writer.WriteLineAsync("250 2.1.0 OK").ConfigureAwait(false);
                    break;

                case "RCPT":
                    if (!session.HasMailFrom)
                    {
                        await writer.WriteLineAsync("503 5.5.1 MAIL FROM required first").ConfigureAwait(false);
                        break;
                    }
                    if (session.Recipients.Count >= config.MaxRecipientsPerMessage)
                    {
                        await writer.WriteLineAsync("452 4.5.3 Too many recipients").ConfigureAwait(false);
                        break;
                    }
                    if (!TryExtractPath(
                            line,
                            "TO",
                            allowEmpty: false,
                            out var rcpt,
                            out var rcptRequiresSmtpUtf8,
                            out var rcptParameters))
                    {
                        await writer.WriteLineAsync("501 5.1.3 Recipient address syntax is invalid").ConfigureAwait(false);
                        break;
                    }
                    if (!TryParseRecipientDsnParameters(
                            rcptParameters,
                            session.IsExtendedSmtp,
                            session.SmtpUtf8,
                            out var recipientDsn,
                            out var rcptFailure))
                    {
                        await writer.WriteLineAsync(rcptFailure).ConfigureAwait(false);
                        break;
                    }
                    if (rcptRequiresSmtpUtf8 && !session.SmtpUtf8)
                    {
                        await writer.WriteLineAsync(
                            "553 5.6.7 Non-ASCII recipient requires SMTPUTF8").ConfigureAwait(false);
                        break;
                    }
                    bool isLocal;
                    try
                    {
                        isLocal = await application.CanReceiveAsync(
                            new SmtpRecipientCheck(rcpt), timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!timeout.IsCancellationRequested)
                    {
                        LogRecipientPolicyUnavailable(logger, exception);
                        await writer.WriteLineAsync("451 4.3.0 Recipient policy is temporarily unavailable").ConfigureAwait(false);
                        break;
                    }
                    if (isLocal)
                    {
                        AddRecipient(session, rcpt, isLocal: true, recipientDsn);
                        await writer.WriteLineAsync("250 2.1.5 OK").ConfigureAwait(false);
                    }
                    else if (session.IsAuthenticated && config.AllowRelay)
                    {
                        AddRecipient(session, rcpt, isLocal: false, recipientDsn);
                        await writer.WriteLineAsync("250 2.1.5 OK").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("550 5.1.1 No such user").ConfigureAwait(false);
                    }
                    break;

                case "DATA":
                    if (session.Recipients.Count == 0)
                    {
                        await writer.WriteLineAsync("503 5.5.1 No valid recipients").ConfigureAwait(false);
                    }
                    else if (!session.TryEnterDataMode(_dataTransactionLimiter))
                    {
                        await writer.WriteLineAsync("452 4.3.2 Too many concurrent message transfers").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("354 Start mail input; end with <CRLF>.<CRLF>").ConfigureAwait(false);
                    }
                    break;

                case "STARTTLS":
                    if (session.IsSecure)
                    {
                        await writer.WriteLineAsync("503 5.5.1 TLS is already active").ConfigureAwait(false);
                    }
                    else if (config.EnableStartTls && config.TlsCertificatePath is not null
                        && upgradableStream is not null)
                    {
                        await writer.WriteLineAsync("220 Ready to start TLS").ConfigureAwait(false);
                        await writer.FlushAsync(timeout.Token).ConfigureAwait(false);

                        using var cert = LoadCertificate(config);
                        var tlsStream = new SslStream(upgradableStream, leaveInnerStreamOpen: false);
                        await using var tlsStreamLifetime = tlsStream.ConfigureAwait(false);
                        if (!await TryAuthenticateAsServerAsync(
                                tlsStream,
                                cert,
                                clientIp ?? "unknown",
                                timeout.Token).ConfigureAwait(false))
                        {
                            return;
                        }

                        using var tlsStreamReader = new StreamReader(tlsStream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                        var tlsReader = new BoundedLineReader(tlsStreamReader);
                        var tlsWriter = new StreamWriter(tlsStream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
                        {
                            AutoFlush = true,
                            NewLine = "\r\n"
                        };
                        await using var tlsWriterLifetime = tlsWriter.ConfigureAwait(false);

                        session.Reset();
                        session.AuthenticatedUser = null;
                        session.Helo = null;
                        session.IsExtendedSmtp = false;
                        session.IsSecure = true;

                        await RunSmtpSessionAsync(
                            tlsReader,
                            tlsWriter,
                            session,
                            application,
                            config,
                            timeout,
                            clientIp: clientIp).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        await writer.WriteLineAsync("502 STARTTLS not enabled").ConfigureAwait(false);
                    }
                    break;

                case "RSET":
                    session.Reset();
                    await writer.WriteLineAsync("250 2.0.0 OK").ConfigureAwait(false);
                    break;

                case "NOOP":
                    await writer.WriteLineAsync("250 2.0.0 OK").ConfigureAwait(false);
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 2.0.0 Bye").ConfigureAwait(false);
                    return;

                case "VRFY":
                    await writer.WriteLineAsync("252 2.5.2 Cannot VRFY user, but will accept message").ConfigureAwait(false);
                    break;

                case "EXPN":
                    await writer.WriteLineAsync("252 2.5.2 Cannot supply mailing list info").ConfigureAwait(false);
                    break;

                default:
                    await writer.WriteLineAsync("502 5.5.1 Command not implemented").ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task WriteEhloAsync(StreamWriter writer, SmtpListenerOptions config, bool isSecure)
    {
        await writer.WriteLineAsync($"250-{config.SmtpHostname}").ConfigureAwait(false);
        await writer.WriteLineAsync($"250-SIZE {config.MaxMessageSizeBytes}").ConfigureAwait(false);
        await writer.WriteLineAsync("250-8BITMIME").ConfigureAwait(false);
        await writer.WriteLineAsync("250-SMTPUTF8").ConfigureAwait(false);
        await writer.WriteLineAsync("250-DSN").ConfigureAwait(false);
        await writer.WriteLineAsync("250-PIPELINING").ConfigureAwait(false);
        await writer.WriteLineAsync("250-ENHANCEDSTATUSCODES").ConfigureAwait(false);
        if (config.EnableStartTls && !isSecure)
            await writer.WriteLineAsync("250-STARTTLS").ConfigureAwait(false);
        if (isSecure)
        {
            var mechanisms = env.OAuth.EnableOAuth
                ? "PLAIN LOGIN XOAUTH2"
                : "PLAIN LOGIN";
            await writer.WriteLineAsync($"250-AUTH {mechanisms}").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
    }

    private static X509Certificate2 LoadCertificate(SmtpListenerOptions config)
    {
        if (config.TlsCertificateKeyPath is not null)
            return X509Certificate2.CreateFromPemFile(config.TlsCertificatePath!, config.TlsCertificateKeyPath);
        return X509CertificateLoader.LoadPkcs12FromFile(config.TlsCertificatePath!, password: null);
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
                    // The server's minimum TLS policy is 1.2; OS defaults may allow older versions.
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
                var exceptionType = exception.GetType().Name;
                LogTlsHandshakeEnded(logger, remoteLabel, exceptionType);
            }
            return false;
        }
    }

    // SASL mechanism negotiation shares the same attempt and failure state.
#pragma warning disable MA0051
    private async Task HandleAuthAsync(
        string line, BoundedLineReader reader, StreamWriter writer,
        SmtpSession session, ISmtpApplicationService application,
        string clientIp, CancellationToken ct)
    {
#pragma warning restore MA0051
        if (session.IsAuthenticated)
        {
            await writer.WriteLineAsync("503 5.5.1 Already authenticated").ConfigureAwait(false);
            return;
        }

        var parts = line.Split(' ', 3);
        if (parts.Length < 2)
        {
            await writer.WriteLineAsync("501 5.5.4 Syntax error").ConfigureAwait(false);
            return;
        }

        var mechanism = parts[1].ToUpperInvariant();

        string? username = null;
        string? password = null;

        switch (mechanism)
        {
            case "XOAUTH2" when env.OAuth.EnableOAuth:
                {
                    var encoded = parts.Length == 3 ? parts[2] : null;
                    if (encoded is null)
                    {
                        await writer.WriteLineAsync("334 ").ConfigureAwait(false);
                        var encodedResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
                        encoded = encodedResult.Value;
                        if (encodedResult.IsTooLong)
                        {
                            await writer.WriteLineAsync("501 Authentication response is too long").ConfigureAwait(false);
                            return;
                        }
                    }
                    if (encoded is null or "*")
                    {
                        await writer.WriteLineAsync("501 Authentication cancelled").ConfigureAwait(false);
                        return;
                    }
                    if (!OAuthSasl.TryParseXOAuth2(encoded, out var oauthUsername, out var accessToken))
                    {
                        RecordAuthenticationFailure(session, clientIp);
                        await writer.WriteLineAsync("535 5.7.8 Authentication failed").ConfigureAwait(false);
                        return;
                    }

                    SmtpIdentityResult oauthUser;
                    try
                    {
                        oauthUser = await application.AuthenticateOAuthAsync(
                            new SmtpOAuthAuthentication(accessToken), ct).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!ct.IsCancellationRequested)
                    {
                        LogAuthenticationUnavailable(logger, exception);
                        await writer.WriteLineAsync("454 4.7.0 Authentication service is temporarily unavailable").ConfigureAwait(false);
                        return;
                    }
                    if (oauthUser.Username is null
                        || !string.Equals(
                            oauthUsername,
                            oauthUser.Username,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        RecordAuthenticationFailure(session, clientIp);
                        await writer.WriteLineAsync("535 5.7.8 Authentication failed").ConfigureAwait(false);
                        return;
                    }

                    session.AuthenticatedUser = oauthUser.Username;
                    await writer.WriteLineAsync("235 2.7.0 Authentication successful").ConfigureAwait(false);
                    return;
                }

            case "PLAIN":
                {
                    var encoded = parts.Length == 3 ? parts[2] : null;
                    if (encoded is null)
                    {
                        await writer.WriteLineAsync("334 ").ConfigureAwait(false);
                        var encodedResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
                        encoded = encodedResult.Value;
                        if (encodedResult.IsTooLong)
                        {
                            await writer.WriteLineAsync("501 Authentication response is too long").ConfigureAwait(false);
                            return;
                        }
                        if (encoded is null || string.Equals(encoded, "*", StringComparison.Ordinal))
                        {
                            await writer.WriteLineAsync("501 Authentication cancelled").ConfigureAwait(false);
                            return;
                        }
                    }

                    try
                    {
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                        var fields = decoded.Split('\0');
                        if (fields.Length >= 3)
                        {
                            username = string.IsNullOrEmpty(fields[0]) ? fields[1] : fields[0];
                            password = fields[2];
                        }
                    }
                    catch (FormatException)
                    {
                        await writer.WriteLineAsync("501 Invalid base64").ConfigureAwait(false);
                        return;
                    }
                    break;
                }

            case "LOGIN":
                {
                    await writer.WriteLineAsync("334 VXNlcm5hbWU6").ConfigureAwait(false);
                    var userResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
                    var userB64 = userResult.Value;
                    if (userResult.IsTooLong) { await writer.WriteLineAsync("501 Authentication response is too long").ConfigureAwait(false); return; }
                    if (userB64 is null or "*") { await writer.WriteLineAsync("501 Authentication cancelled").ConfigureAwait(false); return; }

                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6").ConfigureAwait(false);
                    var passwordResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
                    var passB64 = passwordResult.Value;
                    if (passwordResult.IsTooLong) { await writer.WriteLineAsync("501 Authentication response is too long").ConfigureAwait(false); return; }
                    if (passB64 is null or "*") { await writer.WriteLineAsync("501 Authentication cancelled").ConfigureAwait(false); return; }

                    try
                    {
                        username = Encoding.UTF8.GetString(Convert.FromBase64String(userB64));
                        password = Encoding.UTF8.GetString(Convert.FromBase64String(passB64));
                    }
                    catch (FormatException)
                    {
                        await writer.WriteLineAsync("501 Invalid base64").ConfigureAwait(false);
                        return;
                    }
                    break;
                }

            default:
                await writer.WriteLineAsync("504 Unsupported authentication mechanism").ConfigureAwait(false);
                return;
        }

        if (username is null || password is null)
        {
            RecordAuthenticationFailure(session, clientIp);
            await writer.WriteLineAsync("535 5.7.8 Authentication failed").ConfigureAwait(false);
            return;
        }

        SmtpIdentityResult user;
        try
        {
            user = await application.AuthenticatePasswordAsync(
                new SmtpPasswordAuthentication(username, password), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            LogAuthenticationUnavailable(logger, exception);
            await writer.WriteLineAsync("454 4.7.0 Authentication service is temporarily unavailable").ConfigureAwait(false);
            return;
        }
        if (user.Username is null)
        {
            RecordAuthenticationFailure(session, clientIp);
            await writer.WriteLineAsync("535 5.7.8 Authentication failed").ConfigureAwait(false);
            return;
        }

        session.AuthenticatedUser = user.Username;
        await writer.WriteLineAsync("235 2.7.0 Authentication successful").ConfigureAwait(false);
    }

    private void RecordAuthenticationFailure(SmtpSession session, string clientIp)
    {
        session.AuthenticationFailures++;
        LogAuthenticationFailed(logger, clientIp);
    }

    private static void AddRecipient(
        SmtpSession session,
        string recipient,
        bool isLocal,
        MailDsnRecipient? dsn)
    {
        if (session.Recipients.Any(item =>
                string.Equals(item.Address, recipient, StringComparison.OrdinalIgnoreCase)))
            return;

        session.Recipients.Add(new MailEnvelopeRecipient(recipient, isLocal, dsn));
    }

    private sealed record ParsedMailCommand(
        string Address,
        bool SmtpUtf8,
        bool BodyIsEightBit,
        string? DsnReturnContent,
        string? DsnEnvelopeId);

    // MAIL FROM extension parsing must preserve its protocol validation order.
#pragma warning disable MA0051
    private static bool TryParseMailCommand(
        string line,
        bool isExtendedSmtp,
        bool allowEmpty,
        int maximumMessageSize,
        out ParsedMailCommand command,
        out string failureResponse)
    {
#pragma warning restore MA0051
        command = new ParsedMailCommand(string.Empty, false, false, null, null);
        failureResponse = "501 5.1.7 Sender address syntax is invalid";
        if (!TryExtractPath(
                line,
                "FROM",
                allowEmpty,
                out var address,
                out var addressRequiresSmtpUtf8,
                out var parameters))
        {
            return false;
        }

        var smtpUtf8 = false;
        var bodyIsEightBit = false;
        var seenBody = false;
        var seenSize = false;
        string? dsnReturnContent = null;
        string? dsnEnvelopeId = null;
        foreach (var parameter in parameters)
        {
            if (!isExtendedSmtp)
            {
                failureResponse = "555 5.5.4 MAIL FROM parameters require EHLO";
                return false;
            }

            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            var name = (equals < 0 ? parameter : parameter[..equals]).ToUpperInvariant();
            var value = equals < 0 ? null : parameter[(equals + 1)..];
            switch (name)
            {
                case "SMTPUTF8":
                    if (smtpUtf8 || value is not null)
                    {
                        failureResponse = "501 5.5.4 Invalid SMTPUTF8 parameter";
                        return false;
                    }
                    smtpUtf8 = true;
                    break;

                case "BODY":
                    if (seenBody
                        || value is null
                        || (!value.Equals("7BIT", StringComparison.OrdinalIgnoreCase)
                            && !value.Equals("8BITMIME", StringComparison.OrdinalIgnoreCase)))
                    {
                        failureResponse = "501 5.5.4 Invalid BODY parameter";
                        return false;
                    }
                    seenBody = true;
                    bodyIsEightBit = value.Equals("8BITMIME", StringComparison.OrdinalIgnoreCase);
                    break;

                case "SIZE":
                    if (seenSize
                        || value is null
                        || !long.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var declaredSize))
                    {
                        failureResponse = "501 5.5.4 Invalid SIZE parameter";
                        return false;
                    }
                    seenSize = true;
                    if (declaredSize > maximumMessageSize)
                    {
                        failureResponse = "552 5.3.4 Message exceeds server limits";
                        return false;
                    }
                    break;

                case "RET":
                    if (dsnReturnContent is not null
                        || value is null
                        || !SmtpDsn.TryNormalizeReturnContent(value, out dsnReturnContent))
                    {
                        failureResponse = "501 5.5.4 Invalid RET parameter";
                        return false;
                    }
                    break;

                case "ENVID":
                    if (dsnEnvelopeId is not null
                        || value is null
                        || !SmtpDsn.TryValidateEnvelopeId(value))
                    {
                        failureResponse = "501 5.5.4 Invalid ENVID parameter";
                        return false;
                    }
                    dsnEnvelopeId = value;
                    break;

                default:
                    failureResponse = "555 5.5.4 Unsupported MAIL FROM parameter";
                    return false;
            }
        }

        if (addressRequiresSmtpUtf8 && !smtpUtf8)
        {
            failureResponse = "550 5.6.7 Non-ASCII sender requires SMTPUTF8";
            return false;
        }

        command = new ParsedMailCommand(
            address,
            smtpUtf8,
            bodyIsEightBit,
            dsnReturnContent,
            dsnEnvelopeId);
        return true;
    }

    private static bool TryParseRecipientDsnParameters(
        IReadOnlyList<string> parameters,
        bool isExtendedSmtp,
        bool smtpUtf8,
        out MailDsnRecipient? dsn,
        out string failureResponse)
    {
        dsn = null;
        failureResponse = "501 5.5.4 Invalid RCPT TO parameter";
        string? notify = null;
        string? originalRecipient = null;
        foreach (var parameter in parameters)
        {
            if (!isExtendedSmtp)
            {
                failureResponse = "555 5.5.4 RCPT TO parameters require EHLO";
                return false;
            }

            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            var name = (equals < 0 ? parameter : parameter[..equals]).ToUpperInvariant();
            var value = equals < 0 ? null : parameter[(equals + 1)..];
            switch (name)
            {
                case "NOTIFY":
                    if (notify is not null
                        || value is null
                        || !SmtpDsn.TryNormalizeNotify(value, out notify))
                    {
                        return false;
                    }
                    break;

                case "ORCPT":
                    if (originalRecipient is not null
                        || value is null
                        || parameter.Length > 500
                        || !SmtpDsn.TryValidateOriginalRecipient(
                            value,
                            smtpUtf8,
                            out _,
                            out _))
                    {
                        return false;
                    }
                    originalRecipient = value;
                    break;

                default:
                    failureResponse = "555 5.5.4 Unsupported RCPT TO parameter";
                    return false;
            }
        }

        if (notify is not null || originalRecipient is not null)
            dsn = new MailDsnRecipient(notify, originalRecipient);
        return true;
    }

    private static bool TryExtractPath(
        string line,
        string pathName,
        bool allowEmpty,
        out string address,
        out bool requiresSmtpUtf8,
        out IReadOnlyList<string> parameters)
    {
        address = string.Empty;
        requiresSmtpUtf8 = false;
        parameters = [];
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
            return false;

        var prefix = line[..colon].Trim();
        if (!prefix.Equals($"{(string.Equals(pathName, "FROM", StringComparison.Ordinal) ? "MAIL" : "RCPT")} {pathName}", StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = line[(colon + 1)..].TrimStart();
        string candidate;
        if (remainder.StartsWith('<'))
        {
            var close = FindPathClose(remainder);
            if (close < 0)
                return false;
            candidate = remainder[1..close];
            remainder = remainder[(close + 1)..];
        }
        else
        {
            var separator = remainder.IndexOf(' ', StringComparison.Ordinal);
            candidate = separator < 0 ? remainder : remainder[..separator];
            remainder = separator < 0 ? string.Empty : remainder[separator..];
        }

        if (remainder.Length > 0 && remainder[0] is not (' ' or '\t'))
            return false;

        var parsedParameters = remainder.TrimStart()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parsedParameters.Any(parameter => !IsSafeEsmtpParameter(parameter)))
        {
            return false;
        }

        parameters = parsedParameters;
        return SmtpAddress.TryNormalize(
            candidate,
            allowEmpty,
            out address,
            out requiresSmtpUtf8);
    }

    private static int FindPathClose(string value)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && character == '>')
                return index;
        }
        return -1;
    }

    private static bool IsSafeEsmtpParameter(string value) =>
        value.Length is > 0 and <= 1024
        && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
        && !value.ContainsAny(['\r', '\n', '\0', '<', '>']);

    private static bool TryGetGreeting(string line, out string greeting)
    {
        greeting = string.Empty;
        var separator = line.IndexOf(' ', StringComparison.Ordinal);
        if (separator < 0)
            return false;

        var value = line[(separator + 1)..].Trim();
        if (value.Length is 0 or > 255
            || !value.All(char.IsAscii)
            || value.ContainsAny(['\r', '\n', '\0', ' ', '\t']))
        {
            return false;
        }

        greeting = value;
        return true;
    }

    private static string BuildReceivedHeader(
        Guid queueId,
        string host,
        string? helo,
        string? clientIp,
        bool isSecure,
        bool isAuthenticated,
        bool isExtendedSmtp,
        bool smtpUtf8)
    {
        var protocol = smtpUtf8
            ? "UTF8SMTP"
            : isExtendedSmtp ? "ESMTP" : "SMTP";
        if (isSecure)
            protocol += "S";
        if (isAuthenticated)
            protocol += "A";

        var source = string.IsNullOrWhiteSpace(helo) ? "unknown" : helo;
        var address = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        return $"Received: from {source} ([{address}])\r\n" +
               $"\tby {host} with {protocol} id {queueId:N}; {DateTimeOffset.UtcNow:r}\r\n";
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "No SMTP listeners are enabled.")]
    private static partial void LogNoListeners(ILogger logger);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Information,
        Message = "SMTP {Mode} listener started on port {Port}")]
    private static partial void LogListenerStarted(ILogger logger, ListenerMode mode, int port);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Information,
        Message = "SMTP {Mode} listener on port {Port} stopped.")]
    private static partial void LogListenerStopped(ILogger logger, ListenerMode mode, int port);

    [LoggerMessage(EventId = 3204, Level = LogLevel.Warning,
        Message = "Rejected SMTP connection from {Endpoint}: connection limit")]
    private static partial void LogConnectionLimit(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3205, Level = LogLevel.Debug,
        Message = "SMTP connection from {Endpoint} timed out")]
    private static partial void LogConnectionTimedOut(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3206, Level = LogLevel.Warning,
        Message = "Error handling SMTP connection from {Endpoint}")]
    private static partial void LogConnectionError(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 3207, Level = LogLevel.Warning,
        Message = "SMTP sender policy is temporarily unavailable")]
    private static partial void LogSenderPolicyUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3208, Level = LogLevel.Error,
        Message = "Could not persist SMTP queue message {QueueId}")]
    private static partial void LogQueuePersistFailed(ILogger logger, Exception exception, Guid queueId);

    [LoggerMessage(EventId = 3209, Level = LogLevel.Information,
        Message = "Accepted SMTP queue message {QueueId} with {RecipientCount} recipients")]
    private static partial void LogQueueAccepted(ILogger logger, Guid queueId, int recipientCount);

    [LoggerMessage(EventId = 3210, Level = LogLevel.Warning,
        Message = "SMTP recipient policy is temporarily unavailable")]
    private static partial void LogRecipientPolicyUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3211, Level = LogLevel.Debug,
        Message = "SMTP TLS handshake from {Endpoint} ended before authentication: {ExceptionType}")]
    private static partial void LogTlsHandshakeEnded(ILogger logger, string endpoint, string exceptionType);

    [LoggerMessage(EventId = 3212, Level = LogLevel.Warning,
        Message = "SMTP authentication service is temporarily unavailable")]
    private static partial void LogAuthenticationUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3213, Level = LogLevel.Warning,
        Message = "Mail authentication failed for protocol SMTP from {RemoteIp}")]
    private static partial void LogAuthenticationFailed(ILogger logger, string remoteIp);
}
