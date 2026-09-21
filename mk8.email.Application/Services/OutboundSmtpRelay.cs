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
                "The message is not in the mail wire byte representation.");
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
                "The envelope address is not valid.");
        }

        var requiresSmtpUtf8 = options?.RequiresSmtpUtf8 == true
            || senderIsInternational
            || recipientIsInternational
            || SmtpInternationalization.HeadersRequireSmtpUtf8(rawMessage);

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
                    "The recipient domain does not accept mail.")
                : new OutboundDeliveryResult(
                    OutboundDeliveryStatus.TemporaryFailure,
                    "Mail routing is temporarily unavailable.");
        }

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
                attemptTimeout.Token);

            if (result == DeliveryAttemptResult.Delivered)
            {
                _logger.LogInformation("Outbound SMTP delivery through {Host} completed", endpoint.Host);
                return new OutboundDeliveryResult(
                    OutboundDeliveryStatus.Delivered,
                    "The remote mail server accepted the message.");
            }

            _logger.LogWarning("Outbound SMTP delivery through {Host} ended with {Result}", endpoint.Host, result);
            if (result == DeliveryAttemptResult.PermanentFailure)
            {
                return new OutboundDeliveryResult(
                    OutboundDeliveryStatus.PermanentFailure,
                    "The remote mail server rejected the message permanently.");
            }
        }

        return new OutboundDeliveryResult(
            OutboundDeliveryStatus.TemporaryFailure,
            "All remote delivery attempts failed temporarily.");
    }

    private async Task<DeliveryAttemptResult> TryDeliverAsync(
        MailExchangeEndpoint endpoint,
        string sender,
        string recipient,
        string rawMessage,
        bool requiresSmtpUtf8,
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
                return DeliveryAttemptResult.TryNextHost;

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
                    return DeliveryAttemptResult.TryNextHost;

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
                return DeliveryAttemptResult.PermanentFailure;
            }

            var containsEightBit = SmtpInternationalization.ContainsEightBit(rawMessage);
            if (containsEightBit
                && (ehlo.Code != 250 || !HasCapability(ehlo, "8BITMIME")))
            {
                return DeliveryAttemptResult.PermanentFailure;
            }

            var mailCommand = new StringBuilder($"MAIL FROM:<{sender}>");
            if (containsEightBit)
                mailCommand.Append(" BODY=8BITMIME");
            if (requiresSmtpUtf8)
                mailCommand.Append(" SMTPUTF8");
            var mail = await SendCommandAsync(
                connection,
                mailCommand.ToString(),
                requiresSmtpUtf8,
                cancellationToken);
            if (mail?.Code / 100 != 2)
                return Classify(mail);

            var recipientResponse = await SendCommandAsync(
                connection,
                $"RCPT TO:<{recipient}>",
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
            return DeliveryAttemptResult.Delivered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Outbound SMTP attempt failed through {Host}", endpoint.Host);
            return DeliveryAttemptResult.TryNextHost;
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

    private static DeliveryAttemptResult Classify(SmtpResponse? response)
    {
        return response?.Code / 100 == 5
            ? DeliveryAttemptResult.PermanentFailure
            : DeliveryAttemptResult.TryNextHost;
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

    private enum DeliveryAttemptResult
    {
        Delivered,
        TryNextHost,
        PermanentFailure,
    }

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
