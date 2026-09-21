using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Application.Services;

public sealed class OutboundSmtpRelay : IOutboundMailRelay
{
    private const int MaximumAttempts = 5;
    private const int MaximumResponseLines = 100;
    private const int MaximumResponseLineCharacters = 4096;
    private static readonly Encoding ProtocolEncoding = MailWireEncoding.Instance;

    private readonly IMailExchangeResolver _resolver;
    private readonly EnvironmentConfig _environment;
    private readonly ILogger<OutboundSmtpRelay> _logger;
    private readonly RemoteCertificateValidationCallback? _certificateValidationCallback;

    public OutboundSmtpRelay(
        IMailExchangeResolver resolver,
        EnvironmentConfig environment,
        ILogger<OutboundSmtpRelay> logger)
        : this(resolver, environment, logger, certificateValidationCallback: null)
    {
    }

    internal OutboundSmtpRelay(
        IMailExchangeResolver resolver,
        EnvironmentConfig environment,
        ILogger<OutboundSmtpRelay> logger,
        RemoteCertificateValidationCallback? certificateValidationCallback)
    {
        _resolver = resolver;
        _environment = environment;
        _logger = logger;
        _certificateValidationCallback = certificateValidationCallback;
    }

    public async Task<OutboundDeliveryResult> RelayAsync(
        string sender,
        string recipient,
        string rawMessage,
        OutboundMailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (rawMessage.Any(character => character > byte.MaxValue))
        {
            return new OutboundDeliveryResult(
                OutboundDeliveryStatus.PermanentFailure,
                "The message is not in the mail wire byte representation.",
                EnhancedStatusCode: "5.6.0");
        }

        if (!SmtpAddress.TryNormalize(sender, allowEmpty: true, out sender, out var senderIsInternational)
            || !SmtpAddress.TryNormalize(
                recipient,
                allowEmpty: false,
                out recipient,
                out var recipientIsInternational)
            || !TryGetDomain(recipient, out var domain))
        {
            return new OutboundDeliveryResult(
                OutboundDeliveryStatus.PermanentFailure,
                "The envelope address is not valid.",
                EnhancedStatusCode: "5.1.3");
        }

        var requiresSmtpUtf8 = options?.RequiresSmtpUtf8 == true
            || senderIsInternational
            || recipientIsInternational
            || SmtpInternationalization.HeadersRequireSmtpUtf8(rawMessage);
        if (!TryNormalizeDsnOptions(options, requiresSmtpUtf8, out options))
        {
            return new OutboundDeliveryResult(
                OutboundDeliveryStatus.PermanentFailure,
                "The delivery status notification options are not valid.",
                EnhancedStatusCode: "5.5.4");
        }

        var timeoutSeconds = Math.Clamp(_environment.Limits.ConnectionTimeoutSeconds, 10, 60);
        using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookupTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var route = await _resolver.ResolveAsync(domain, lookupTimeout.Token);
        if (route.Status != MailRoutingStatus.Available)
        {
            _logger.LogWarning("Mail routing is unavailable for {Domain}: {Status}", domain, route.Status);
            return route.Status == MailRoutingStatus.DoesNotAcceptMail
                ? new OutboundDeliveryResult(
                    OutboundDeliveryStatus.PermanentFailure,
                    "The recipient domain does not accept mail.",
                    EnhancedStatusCode: "5.1.2")
                : new OutboundDeliveryResult(
                    OutboundDeliveryStatus.TemporaryFailure,
                    "Mail routing is temporarily unavailable.",
                    EnhancedStatusCode: "4.4.3");
        }

        DeliveryAttempt? lastTemporaryFailure = null;
        string? lastTemporaryHost = null;
        foreach (var endpoint in route.Exchanges
                     .Where(IsUsableEndpoint)
                     .OrderBy(endpoint => endpoint.Preference)
                     .Take(MaximumAttempts))
        {
            if (string.Equals(endpoint.Host, _environment.Smtp.Hostname, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipped outbound mail loop through {Host}", endpoint.Host);
                continue;
            }

            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var result = await TryDeliverAsync(
                endpoint,
                sender,
                recipient,
                rawMessage,
                requiresSmtpUtf8,
                options,
                attemptTimeout.Token);

            if (result.Status == DeliveryAttemptStatus.Delivered)
            {
                _logger.LogInformation("Outbound SMTP delivery through {Host} completed", endpoint.Host);
                return new OutboundDeliveryResult(
                    OutboundDeliveryStatus.Delivered,
                    "The remote mail server accepted the message.",
                    result.DsnParametersForwarded,
                    endpoint.Host,
                    result.EnhancedStatusCode);
            }

            _logger.LogWarning(
                "Outbound SMTP delivery through {Host} ended with {Result}",
                endpoint.Host,
                result.Status);
            if (result.Status == DeliveryAttemptStatus.PermanentFailure)
            {
                return new OutboundDeliveryResult(
                    OutboundDeliveryStatus.PermanentFailure,
                    result.Detail
                        ?? "The remote mail server rejected the message permanently.",
                    RemoteMta: endpoint.Host,
                    EnhancedStatusCode: result.EnhancedStatusCode);
            }

            lastTemporaryFailure = result;
            lastTemporaryHost = endpoint.Host;
        }

        return new OutboundDeliveryResult(
            OutboundDeliveryStatus.TemporaryFailure,
            lastTemporaryFailure?.Detail
                ?? "All remote delivery attempts failed temporarily.",
            RemoteMta: lastTemporaryHost,
            EnhancedStatusCode: lastTemporaryFailure?.EnhancedStatusCode ?? "4.4.1");
    }

    private async Task<DeliveryAttempt> TryDeliverAsync(
        MailExchangeEndpoint endpoint,
        string sender,
        string recipient,
        string rawMessage,
        bool requiresSmtpUtf8,
        OutboundMailOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await SmtpConnection.ConnectAsync(
                endpoint,
                _certificateValidationCallback,
                cancellationToken);

            var greeting = await connection.ReadResponseAsync(cancellationToken);
            if (greeting?.Code != 220)
                return Classify(greeting);

            var ehlo = await SendCommandAsync(
                connection,
                $"EHLO {_environment.Smtp.Hostname}",
                cancellationToken);
            if (ehlo is null)
                return new DeliveryAttempt(DeliveryAttemptStatus.TryNextHost);

            if (ehlo.Code != 250)
            {
                if (ehlo.Code / 100 != 5)
                    return Classify(ehlo);

                var helo = await SendCommandAsync(
                    connection,
                    $"HELO {_environment.Smtp.Hostname}",
                    cancellationToken);
                if (helo?.Code != 250)
                    return Classify(helo);
            }
            else if (HasCapability(ehlo, "STARTTLS"))
            {
                var startTls = await SendCommandAsync(connection, "STARTTLS", cancellationToken);
                if (startTls?.Code != 220)
                    return new DeliveryAttempt(DeliveryAttemptStatus.TryNextHost);

                await connection.UpgradeToTlsAsync(endpoint.Host, cancellationToken);
                ehlo = await SendCommandAsync(
                    connection,
                    $"EHLO {_environment.Smtp.Hostname}",
                    cancellationToken);
                if (ehlo?.Code != 250)
                    return Classify(ehlo);
            }

            if (requiresSmtpUtf8
                && (ehlo.Code != 250 || !HasCapability(ehlo, "SMTPUTF8")))
            {
                return new DeliveryAttempt(
                    DeliveryAttemptStatus.PermanentFailure,
                    "5.6.7",
                    Detail: "550 5.6.7 The next hop does not support SMTPUTF8.");
            }

            var containsEightBit = SmtpInternationalization.ContainsEightBit(rawMessage);
            if (containsEightBit
                && (ehlo.Code != 250 || !HasCapability(ehlo, "8BITMIME")))
            {
                return new DeliveryAttempt(
                    DeliveryAttemptStatus.PermanentFailure,
                    "5.6.3",
                    Detail: "550 5.6.3 The next hop does not support 8BITMIME.");
            }

            var supportsDsn = ehlo.Code == 250 && HasCapability(ehlo, "DSN");
            var transmittedSender = !supportsDsn
                && SmtpDsn.SuppressesAll(options?.RecipientDsn?.Notify)
                    ? string.Empty
                    : sender;
            var mailCommand = new StringBuilder($"MAIL FROM:<{transmittedSender}>");
            if (containsEightBit)
                mailCommand.Append(" BODY=8BITMIME");
            if (requiresSmtpUtf8)
                mailCommand.Append(" SMTPUTF8");
            if (supportsDsn && options?.Dsn?.ReturnContent is not null)
                mailCommand.Append(" RET=").Append(options.Dsn.ReturnContent);
            if (supportsDsn && options?.Dsn?.EnvelopeId is not null)
                mailCommand.Append(" ENVID=").Append(options.Dsn.EnvelopeId);
            var mail = await SendCommandAsync(
                connection,
                mailCommand.ToString(),
                requiresSmtpUtf8,
                cancellationToken);
            if (mail?.Code / 100 != 2)
                return Classify(mail);

            var recipientCommand = new StringBuilder($"RCPT TO:<{recipient}>");
            if (supportsDsn && options?.RecipientDsn?.Notify is not null)
                recipientCommand.Append(" NOTIFY=").Append(options.RecipientDsn.Notify);
            if (supportsDsn && options?.RecipientDsn?.OriginalRecipient is not null)
                recipientCommand.Append(" ORCPT=").Append(options.RecipientDsn.OriginalRecipient);
            var recipientResponse = await SendCommandAsync(
                connection,
                recipientCommand.ToString(),
                requiresSmtpUtf8,
                cancellationToken);
            if (recipientResponse?.Code / 100 != 2)
                return Classify(recipientResponse);

            var data = await SendCommandAsync(connection, "DATA", cancellationToken);
            if (data?.Code != 354)
                return Classify(data);

            await connection.WriteMessageAsync(rawMessage, cancellationToken);
            var completion = await connection.ReadResponseAsync(cancellationToken);
            if (completion?.Code / 100 != 2)
                return Classify(completion);

            await connection.WriteLineAsync("QUIT", cancellationToken);
            var dsnForwarded = supportsDsn
                && (options?.Dsn?.ReturnContent is not null
                    || options?.Dsn?.EnvelopeId is not null
                    || options?.RecipientDsn?.Notify is not null
                    || options?.RecipientDsn?.OriginalRecipient is not null);
            return new DeliveryAttempt(
                DeliveryAttemptStatus.Delivered,
                GetEnhancedStatusCode(completion),
                dsnForwarded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Outbound SMTP attempt failed through {Host}", endpoint.Host);
            return new DeliveryAttempt(DeliveryAttemptStatus.TryNextHost);
        }
    }

    private static async Task<SmtpResponse?> SendCommandAsync(
        SmtpConnection connection,
        string command,
        bool utf8,
        CancellationToken cancellationToken)
    {
        if (utf8)
            await connection.WriteUtf8LineAsync(command, cancellationToken);
        else
            await connection.WriteLineAsync(command, cancellationToken);
        return await connection.ReadResponseAsync(cancellationToken);
    }

    private static Task<SmtpResponse?> SendCommandAsync(
        SmtpConnection connection,
        string command,
        CancellationToken cancellationToken) =>
        SendCommandAsync(connection, command, utf8: false, cancellationToken);

    private static DeliveryAttempt Classify(SmtpResponse? response)
    {
        return new DeliveryAttempt(
            response?.Code / 100 == 5
                ? DeliveryAttemptStatus.PermanentFailure
                : DeliveryAttemptStatus.TryNextHost,
            GetEnhancedStatusCode(response),
            Detail: response?.Lines.LastOrDefault());
    }

    private static string? GetEnhancedStatusCode(SmtpResponse? response)
    {
        var line = response?.Lines.LastOrDefault();
        if (line is null || line.Length <= 4)
            return null;
        var token = line[4..].TrimStart().Split(' ', 2)[0];
        var parts = token.Split('.');
        return parts.Length == 3
            && parts[0] is "2" or "4" or "5"
            && parts.Skip(1).All(part =>
                part.Length is >= 1 and <= 3
                && part.All(char.IsAsciiDigit))
            ? token
            : null;
    }

    private static bool TryNormalizeDsnOptions(
        OutboundMailOptions? source,
        bool requiresSmtpUtf8,
        out OutboundMailOptions? normalized)
    {
        normalized = source;
        if (source is null)
            return true;

        string? returnContent = null;
        if (source.Dsn?.ReturnContent is not null
            && !SmtpDsn.TryNormalizeReturnContent(source.Dsn.ReturnContent, out returnContent))
        {
            return false;
        }
        if (source.Dsn?.EnvelopeId is not null
            && !SmtpDsn.TryValidateEnvelopeId(source.Dsn.EnvelopeId))
        {
            return false;
        }

        string? notify = null;
        if (source.RecipientDsn?.Notify is not null
            && !SmtpDsn.TryNormalizeNotify(source.RecipientDsn.Notify, out notify))
        {
            return false;
        }
        if (source.RecipientDsn?.OriginalRecipient is not null
            && !SmtpDsn.TryValidateOriginalRecipient(
                source.RecipientDsn.OriginalRecipient,
                requiresSmtpUtf8,
                out _,
                out _))
        {
            return false;
        }

        normalized = new OutboundMailOptions(
            source.RequiresSmtpUtf8,
            source.Dsn is null
                ? null
                : new MailDsnEnvelope(returnContent, source.Dsn.EnvelopeId),
            source.RecipientDsn is null
                ? null
                : new MailDsnRecipient(notify, source.RecipientDsn.OriginalRecipient));
        return true;
    }

    private static bool HasCapability(SmtpResponse response, string capability)
    {
        return response.Lines.Any(line =>
        {
            var value = line.Length > 4 ? line[4..].Trim() : string.Empty;
            return value.Equals(capability, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(capability + " ", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool TryGetDomain(string recipient, out string domain)
    {
        domain = string.Empty;
        var separator = recipient.LastIndexOf('@');
        if (separator <= 0 || separator == recipient.Length - 1)
            return false;

        domain = recipient[(separator + 1)..].ToLowerInvariant();
        return Uri.CheckHostName(domain) == UriHostNameType.Dns;
    }

    private static bool IsUsableEndpoint(MailExchangeEndpoint endpoint)
    {
        return endpoint.Port is > 0 and <= 65535
            && Uri.CheckHostName(endpoint.Host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private enum DeliveryAttemptStatus
    {
        Delivered,
        TryNextHost,
        PermanentFailure,
    }

    private sealed record DeliveryAttempt(
        DeliveryAttemptStatus Status,
        string? EnhancedStatusCode = null,
        bool DsnParametersForwarded = false,
        string? Detail = null);

    private sealed record SmtpResponse(int Code, IReadOnlyList<string> Lines);

    private sealed class SmtpConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly RemoteCertificateValidationCallback? _certificateValidationCallback;
        private Stream _stream;
        private StreamReader _streamReader;
        private BoundedLineReader _lineReader;
        private StreamWriter _writer;

        private SmtpConnection(
            TcpClient client,
            RemoteCertificateValidationCallback? certificateValidationCallback)
        {
            _client = client;
            _certificateValidationCallback = certificateValidationCallback;
            _stream = client.GetStream();
            _streamReader = CreateStreamReader(_stream);
            _lineReader = new BoundedLineReader(_streamReader);
            _writer = CreateWriter(_stream);
        }

        public static async Task<SmtpConnection> ConnectAsync(
            MailExchangeEndpoint endpoint,
            RemoteCertificateValidationCallback? certificateValidationCallback,
            CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
                return new SmtpConnection(client, certificateValidationCallback);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task<SmtpResponse?> ReadResponseAsync(CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            int? responseCode = null;

            for (var index = 0; index < MaximumResponseLines; index++)
            {
                var readResult = await _lineReader.ReadLineAsync(
                    MaximumResponseLineCharacters,
                    cancellationToken);
                var line = readResult.Value;
                if (readResult.IsTooLong || line is null || line.Length < 3
                    || !int.TryParse(line.AsSpan(0, 3), out var lineCode))
                {
                    return null;
                }

                responseCode ??= lineCode;
                if (responseCode != lineCode)
                    return null;

                lines.Add(line);
                if (line.Length == 3 || line[3] == ' ')
                    return new SmtpResponse(responseCode.Value, lines);
                if (line[3] != '-')
                    return null;
            }

            return null;
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
            _writer.WriteLineAsync(line.AsMemory(), cancellationToken);

        public async Task WriteUtf8LineAsync(string line, CancellationToken cancellationToken)
        {
            await _writer.FlushAsync(cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        public async Task WriteMessageAsync(string rawMessage, CancellationToken cancellationToken)
        {
            var normalized = rawMessage.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            using var messageReader = new StringReader(normalized);

            while (messageReader.ReadLine() is { } line)
            {
                if (line.Length > 0 && line[0] == '.')
                    await _writer.WriteAsync(".".AsMemory(), cancellationToken);
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }

            await _writer.WriteLineAsync(".".AsMemory(), cancellationToken);
        }

        public async Task UpgradeToTlsAsync(string host, CancellationToken cancellationToken)
        {
            await _writer.FlushAsync(cancellationToken);
            _streamReader.Dispose();
            await _writer.DisposeAsync();

            var tlsStream = new SslStream(
                _stream,
                leaveInnerStreamOpen: false,
                _certificateValidationCallback);
            try
            {
                await tlsStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host },
                    cancellationToken);
            }
            catch
            {
                await tlsStream.DisposeAsync();
                throw;
            }

            _stream = tlsStream;
            _streamReader = CreateStreamReader(_stream);
            _lineReader = new BoundedLineReader(_streamReader);
            _writer = CreateWriter(_stream);
        }

        public async ValueTask DisposeAsync()
        {
            _streamReader.Dispose();
            await _writer.DisposeAsync();
            await _stream.DisposeAsync();
            _client.Dispose();
        }

        private static StreamReader CreateStreamReader(Stream stream) =>
            new(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        private static StreamWriter CreateWriter(Stream stream) =>
            new(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };
    }
}
