using System.Text.Json;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Messaging;

namespace mk8.email.Gateway.Protocols.OAuth;

public sealed class OAuthTrafficCaptureMiddleware(
    RequestDelegate next,
    ILogger<OAuthTrafficCaptureMiddleware> logger)
{
    private const int MaximumCapturedBytes = 64 * 1024;
    private const string EnvelopeContentType = "application/vnd.mk8.gateway-http+json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(
        HttpContext context,
        IGatewayTrafficJournal journal,
        GatewayApplicationOptions options)
    {
        if (!GatewayProtocolPaths.IsOAuth(context.Request.Path))
        {
            await next(context);
            return;
        }

        var sessionId = Guid.CreateVersion7();
        var requestPayload = await CaptureRequestAsync(context.Request, context.RequestAborted);
        if (!await TryAppendAsync(
                journal,
                options,
                CreateRecord(
                    sessionId,
                    0,
                    GatewayTrafficDirections.Inbound,
                    requestPayload)))
        {
            await WriteJournalUnavailableAsync(context, context.Response.Body);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capturedBody = new MemoryStream();
        context.Response.Body = capturedBody;
        try
        {
            await next(context);
            var responsePayload = CaptureResponse(context.Response, capturedBody.ToArray());
            if (!await TryAppendAsync(
                    journal,
                    options,
                    CreateRecord(
                        sessionId,
                        1,
                        GatewayTrafficDirections.Outbound,
                        responsePayload)))
            {
                await WriteJournalUnavailableAsync(context, originalBody);
                return;
            }

            capturedBody.Position = 0;
            await capturedBody.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task<byte[]> CaptureRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering(MaximumCapturedBytes, MaximumCapturedBytes);
        await using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        request.Body.Position = 0;
        return JsonSerializer.SerializeToUtf8Bytes(new HttpRequestEnvelope(
            request.Method,
            request.Path.Value ?? string.Empty,
            request.QueryString.Value ?? string.Empty,
            request.Protocol,
            request.ContentType,
            Headers(request.Headers),
            Convert.ToBase64String(body.ToArray())),
            JsonOptions);
    }

    private static byte[] CaptureResponse(HttpResponse response, byte[] body) =>
        JsonSerializer.SerializeToUtf8Bytes(new HttpResponseEnvelope(
            response.StatusCode,
            response.ContentType,
            Headers(response.Headers),
            Convert.ToBase64String(body)),
            JsonOptions);

    private async Task<bool> TryAppendAsync(
        IGatewayTrafficJournal journal,
        GatewayApplicationOptions options,
        GatewayTrafficRecord record)
    {
        using var timeout = new CancellationTokenSource(options.TrafficJournalTimeout);
        try
        {
            await journal.AppendAsync(record, timeout.Token);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not persist {Direction} OAuth presentation traffic",
                record.Direction);
            return false;
        }
    }

    private static async Task WriteJournalUnavailableAsync(
        HttpContext context,
        Stream output)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "5";
        await JsonSerializer.SerializeAsync(
            output,
            new Dictionary<string, object>
            {
                ["error"] = "temporarily_unavailable",
                ["error_description"] = "The gateway traffic journal is unavailable.",
            },
            cancellationToken: context.RequestAborted);
    }

    private static GatewayTrafficRecord CreateRecord(
        Guid sessionId,
        long sequence,
        string direction,
        byte[] payload) =>
        new(
            Guid.CreateVersion7(),
            sessionId,
            sequence,
            direction,
            "oauth",
            EnvelopeContentType,
            payload,
            new Dictionary<string, string>
            {
                ["layer"] = "presentation",
            },
            DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<string, string> Headers(IHeaderDictionary headers) =>
        headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

    private sealed record HttpRequestEnvelope(
        string Method,
        string Path,
        string Query,
        string Protocol,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers,
        string BodyBase64);

    private sealed record HttpResponseEnvelope(
        int Status,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers,
        string BodyBase64);
}
