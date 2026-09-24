using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Configuration;

namespace mk8.email.Application.Services;

public sealed class RspamdMailScanner : IMailScanner, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly HashSet<string> SupportedActions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "no action",
            "greylist",
            "add header",
            "rewrite subject",
            "soft reject",
            "reject",
            "discard",
        };

    private readonly EnvironmentConfig _environment;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public RspamdMailScanner(EnvironmentConfig environment)
        : this(environment, CreateClient(), ownsClient: true)
    {
    }

    internal RspamdMailScanner(
        EnvironmentConfig environment,
        HttpClient client,
        bool ownsClient = false)
    {
        _environment = environment;
        _client = client;
        _ownsClient = ownsClient;
    }

    public async Task<MailScanResult> ScanAsync(
        MailScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_environment.Filtering.TimeoutSeconds));

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            _environment.Filtering.RspamdEndpoint);
        message.Content = new ByteArrayContent(
            MailWireEncoding.Instance.GetBytes(request.RawMessage));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        AddHeader(message, "From", request.EnvelopeSender);
        foreach (var recipient in request.Recipients)
            AddHeader(message, "Rcpt", recipient);
        AddHeader(message, "IP", request.ClientIp);
        AddHeader(message, "Helo", request.Helo);
        AddHeader(message, "Hostname", request.Helo);
        AddHeader(message, "User", request.AuthenticatedUser);
        AddHeader(message, "Queue-Id", request.QueueId.ToString("N"));
        AddHeader(message, "Log-Tag", request.QueueId.ToString("N"));
        AddHeader(message, "MTA-Name", _environment.Smtp.Hostname);
        AddHeader(message, "Flags", "milter");

        using var response = await _client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Rspamd returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (responseStream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(responseStream, buffer, MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            buffer.Position = 0;

            using var document = await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions { MaxDepth = 32 },
                timeout.Token).ConfigureAwait(false);
            return ParseScanResult(
                document.RootElement, request.AuthenticatedUser is not null);
        }
    }

    private MailScanResult ParseScanResult(JsonElement root, bool isAuthenticated)
    {
        var action = GetRequiredString(root, "action").ToMailLowerInvariant();
        if (!SupportedActions.Contains(action))
            throw new InvalidDataException("Rspamd returned an unsupported action.");

        var score = GetRequiredNumber(root, "score");
        var requiredScore = GetRequiredNumber(root, "required_score");
        var symbols = ReadSymbols(root);
        var scannerFailed = symbols.Contains("CLAM_VIRUS_FAIL");
        var isTemporaryFailure = scannerFailed
            || action is "soft reject" or "greylist";
        var isMalware = symbols.Any(symbol =>
            symbol.StartsWith("CLAM_VIRUS", StringComparison.Ordinal)
            && !string.Equals(symbol, "CLAM_VIRUS_FAIL", StringComparison.Ordinal));

        var headers = isTemporaryFailure
            ? string.Empty
            : BuildAddedHeaders(root, isAuthenticated, action, score, requiredScore, symbols);

        return new MailScanResult(
            action,
            score,
            requiredScore,
            symbols,
            headers,
            isMalware,
            isTemporaryFailure);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private string BuildAddedHeaders(
        JsonElement root,
        bool isAuthenticated,
        string action,
        double score,
        double requiredScore,
        HashSet<string> symbols)
    {
        var headers = new StringBuilder();
        if (isAuthenticated)
        {
            var signature = GetRequiredString(root, "dkim-signature");
            headers.Append("DKIM-Signature: ")
                .Append(NormalizeFoldedHeaderValue(signature))
                .Append("\r\n");
        }
        else
        {
            headers.Append("Authentication-Results: ")
                .Append(_environment.Smtp.Hostname)
                .Append("; spf=")
                .Append(GetSpfResult(symbols))
                .Append("; dkim=")
                .Append(GetDkimResult(symbols))
                .Append("; dmarc=")
                .Append(GetDmarcResult(symbols))
                .Append("\r\n");
        }

        var isSpam = action is "add header" or "rewrite subject" or "reject" or "discard";
        headers.Append("X-Spam-Status: ")
            .Append(isSpam ? "Yes" : "No")
            .Append(", score=")
            .Append(score.ToString("0.###", CultureInfo.InvariantCulture))
            .Append(" required=")
            .Append(requiredScore.ToString("0.###", CultureInfo.InvariantCulture))
            .Append("\r\n");

        if (headers.Length > 16 * 1024)
            throw new InvalidDataException("Rspamd returned too much header data.");

        return headers.ToString();
    }

    private static HashSet<string> ReadSymbols(JsonElement root)
    {
        if (!root.TryGetProperty("symbols", out var symbolsElement)
            || symbolsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Rspamd returned no symbol object.");
        }

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbolsElement.EnumerateObject())
        {
            if (symbol.Name.Length is 0 or > 128 || !symbol.Name.All(char.IsAscii))
                throw new InvalidDataException("Rspamd returned an invalid symbol name.");
            symbols.Add(symbol.Name);
        }
        return symbols;
    }

    private static string GetSpfResult(HashSet<string> symbols)
    {
        if (symbols.Contains("R_SPF_ALLOW"))
            return "pass";
        if (symbols.Any(symbol => symbol is "R_SPF_FAIL" or "R_SPF_SOFTFAIL" or "VIOLATED_DIRECT_SPF"))
            return "fail";
        return "none";
    }

    private static string GetDkimResult(HashSet<string> symbols)
    {
        if (symbols.Contains("R_DKIM_ALLOW"))
            return "pass";
        if (symbols.Contains("R_DKIM_REJECT"))
            return "fail";
        return "none";
    }

    private static string GetDmarcResult(HashSet<string> symbols)
    {
        if (symbols.Contains("DMARC_POLICY_ALLOW"))
            return "pass";
        if (symbols.Any(symbol => symbol is "DMARC_POLICY_REJECT" or "DMARC_POLICY_QUARANTINE"))
            return "fail";
        return "none";
    }

    private static string NormalizeFoldedHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 16 * 1024
            || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rspamd returned an invalid DKIM signature.");
        }

        var lines = value.Split('\n');
        if (!lines[0].StartsWith("v=1;", StringComparison.Ordinal))
            throw new InvalidDataException("Rspamd returned an invalid DKIM signature.");
        if (lines.Any(line => line.Length > 998 || line.Contains('\0', StringComparison.Ordinal)))
            throw new InvalidDataException("Rspamd returned an invalid DKIM signature.");
        if (lines.Skip(1).Any(line => line.Length == 0 || line[0] is not (' ' or '\t')))
            throw new InvalidDataException("Rspamd returned an invalid folded DKIM signature.");

        return string.Join("\r\n", lines);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Rspamd returned no {propertyName} value.");
        }

        return element.GetString()!;
    }

    private static double GetRequiredNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value))
        {
            throw new InvalidDataException($"Rspamd returned no valid {propertyName} value.");
        }

        return value;
    }

    private static void AddHeader(HttpRequestMessage message, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (value.ContainsAny(['\r', '\n', '\0']))
            throw new InvalidDataException("Mail scan metadata contains a control character.");
        if (!message.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidDataException("Mail scan metadata could not be added.");
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var block = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("Rspamd returned too much data.");
            await destination.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpClient CreateClient()
    {
        // HttpClient owns this handler (disposeHandler: true); the catch below
        // disposes it if client construction or initialization fails.
#pragma warning disable CA2000
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 4,
            UseCookies = false,
            UseProxy = false,
        };
#pragma warning restore CA2000
        try
        {
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = MaximumResponseBytes,
            };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }
}
