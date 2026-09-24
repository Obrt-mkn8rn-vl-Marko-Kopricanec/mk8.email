using System.Text.Json;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Messaging;

namespace mk8.email.Gateway.Protocols;

public sealed class GatewayProtocolTrafficCaptureMiddleware(
    RequestDelegate next,
    ILogger<GatewayProtocolTrafficCaptureMiddleware> logger)
{
    private const int BufferThresholdBytes = 64 * 1024;
    private const string EnvelopeContentType = "application/vnd.mk8.gateway-http+json";
    private const string StreamChunkContentType = "application/vnd.mk8.gateway-http-chunk+json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(
        HttpContext context,
        IGatewayTrafficJournal journal,
        GatewayApplicationOptions options,
        EnvironmentConfig environment)
    {
        var protocol = GatewayProtocolPaths.GetProtocol(context.Request.Path);
        if (protocol is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var sessionId = Guid.CreateVersion7();
        var requestPayload = await CaptureRequestAsync(
            context,
            context.Request,
            GetCaptureLimit(protocol, environment),
            context.RequestAborted).ConfigureAwait(false);
        if (!await TryAppendAsync(
                journal,
                options,
                CreateRecord(
                    sessionId,
                    0,
                    GatewayTrafficDirections.Inbound,
                    protocol,
                    EnvelopeContentType,
                    requestPayload)).ConfigureAwait(false))
        {
            await WriteJournalUnavailableAsync(context, context.Response.Body, protocol).ConfigureAwait(false);
            return;
        }

        if (GatewayProtocolPaths.IsStreaming(context.Request.Path))
        {
            await CaptureStreamingResponseAsync(context, journal, options, sessionId, protocol).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        var capturedBody = new MemoryStream();
        await using var capturedBodyLifetime = capturedBody.ConfigureAwait(false);
        context.Response.Body = capturedBody;
        try
        {
            await next(context).ConfigureAwait(false);
            var responsePayload = CaptureResponse(context.Response, capturedBody.ToArray());
            if (!await TryAppendAsync(
                    journal,
                    options,
                    CreateRecord(
                        sessionId,
                        1,
                        GatewayTrafficDirections.Outbound,
                        protocol,
                        EnvelopeContentType,
                        responsePayload)).ConfigureAwait(false))
            {
                await WriteJournalUnavailableAsync(context, originalBody, protocol).ConfigureAwait(false);
                return;
            }

            capturedBody.Position = 0;
            await capturedBody.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private async Task CaptureStreamingResponseAsync(
        HttpContext context,
        IGatewayTrafficJournal journal,
        GatewayApplicationOptions options,
        Guid sessionId,
        string protocol)
    {
        var capture = new StreamingCapture(
            this,
            journal,
            options,
            sessionId,
            protocol,
            context.Response);
        context.Response.OnStarting(static state =>
        {
            var streaming = (StreamingCapture)state;
            return streaming.AppendStartAsync();
        }, capture);

        var originalBody = context.Response.Body;
        context.Response.Body = new JournaledResponseStream(originalBody, capture);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static long GetCaptureLimit(string protocol, EnvironmentConfig environment) => string.Equals(protocol, "jmap"
, StringComparison.Ordinal) ? Math.Max(
                environment.Jmap.MaxRequestSizeBytes,
                environment.Jmap.MaxUploadSizeBytes)
            : string.Equals(protocol, "dav"
, StringComparison.Ordinal) ? Math.Max(1_048_576, environment.Dav.MaxResourceSizeBytes)
            : BufferThresholdBytes;

    private static async Task<byte[]> CaptureRequestAsync(
        HttpContext context,
        HttpRequest request,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
            throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
        var body = new MemoryStream();
        try
        {
            await request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
            if (body.Length > maximumBytes)
            {
                throw new BadHttpRequestException(
                    "The request body is too large.",
                    StatusCodes.Status413PayloadTooLarge);
            }
            var content = body.ToArray();
            body.Position = 0;
            request.Body = body;
            context.Response.RegisterForDisposeAsync(body);
            return JsonSerializer.SerializeToUtf8Bytes(new HttpRequestEnvelope(
                request.Method,
                request.Path.Value ?? string.Empty,
                request.QueryString.Value ?? string.Empty,
                request.Protocol,
                request.ContentType,
                Headers(request.Headers),
                Convert.ToBase64String(content)),
                JsonOptions);
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
            await journal.AppendAsync(record, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not persist {Direction} {Protocol} presentation traffic",
                record.Direction,
                record.Protocol);
            return false;
        }
    }

    private static async Task WriteJournalUnavailableAsync(
        HttpContext context,
        Stream output,
        string protocol)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "5";
        if (string.Equals(protocol, "oauth", StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                output,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["error"] = "temporarily_unavailable",
                    ["error_description"] = "The gateway traffic journal is unavailable.",
                },
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/problem+json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            output,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "about:blank",
                ["title"] = "Gateway traffic journal unavailable",
                ["status"] = StatusCodes.Status503ServiceUnavailable,
            },
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private static GatewayTrafficRecord CreateRecord(
        Guid sessionId,
        long sequence,
        string direction,
        string protocol,
        string contentType,
        byte[] payload) =>
        new(
            Guid.CreateVersion7(),
            sessionId,
            sequence,
            direction,
            protocol,
            contentType,
            payload,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["layer"] = "presentation",
            },
            DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<string, string> Headers(IHeaderDictionary headers) =>
        headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

    private sealed class StreamingCapture(
        GatewayProtocolTrafficCaptureMiddleware owner,
        IGatewayTrafficJournal journal,
        GatewayApplicationOptions options,
        Guid sessionId,
        string protocol,
        HttpResponse response)
    {
        private long _sequence = 1;
        private int _startRecorded;

        public Task AppendStartAsync()
        {
            if (Interlocked.Exchange(ref _startRecorded, 1) != 0)
                return Task.CompletedTask;
            return AppendAsync(
                EnvelopeContentType,
                CaptureResponse(response, []));
        }

        public async Task AppendChunkAsync(ReadOnlyMemory<byte> content)
        {
            await AppendStartAsync().ConfigureAwait(false);
            await AppendAsync(
                StreamChunkContentType,
                JsonSerializer.SerializeToUtf8Bytes(new HttpStreamChunkEnvelope(
                    response.ContentType,
                    Convert.ToBase64String(content.Span)),
                    JsonOptions)).ConfigureAwait(false);
        }

        private async Task AppendAsync(string contentType, byte[] payload)
        {
            var sequence = Interlocked.Increment(ref _sequence) - 1;
            if (!await owner.TryAppendAsync(
                    journal,
                    options,
                    CreateRecord(
                        sessionId,
                        sequence,
                        GatewayTrafficDirections.Outbound,
                        protocol,
                        contentType,
                        payload)).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The gateway could not durably record streaming presentation traffic.");
            }
        }
    }

    private sealed class JournaledResponseStream(
        Stream inner,
        StreamingCapture capture) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Gateway traffic capture requires asynchronous writes.");

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await capture.AppendChunkAsync(buffer).ConfigureAwait(false);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
    }

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

    private sealed record HttpStreamChunkEnvelope(
        string? ContentType,
        string BodyBase64);
}
