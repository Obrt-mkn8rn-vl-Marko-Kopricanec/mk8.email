using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Messaging;

namespace mk8.email.Gateway.Protocols;

public sealed class GatewayProtocolTrafficCaptureMiddleware(
    RequestDelegate next,
    ILogger<GatewayProtocolTrafficCaptureMiddleware> logger)
{
    private const int ReadBlockBytes = 16 * 1024;
    private const string EnvelopeContentType = "application/vnd.mk8.gateway-http+json";
    private const string StreamChunkContentType = "application/vnd.mk8.gateway-http-chunk+json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The durable presentation boundary keeps its ordered validation, journaling and failure handling together.")]
    public async Task InvokeAsync(
        HttpContext context,
        IGatewayTrafficJournal journal,
        GatewayApplicationOptions options,
        EnvironmentConfig environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var protocol = GatewayProtocolPaths.GetProtocol(context.Request.Path);
        if (protocol is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var sessionId = Guid.CreateVersion7();
        var maximumBytes = GetCaptureLimit(context.Request.Path, protocol, environment);
        var requestBody = new GatewayCaptureBuffer(maximumBytes, StatusCodes.Status413PayloadTooLarge);
        await using var requestBodyLifetime = requestBody.ConfigureAwait(false);
        var originalRequestBody = context.Request.Body;
        var capturedRequest = await CaptureRequestAsync(context, requestBody, maximumBytes, environment.Messaging.MaxPayloadBytes)
            .ConfigureAwait(false);
        if (!await TryAppendAsync(
                journal,
                options,
                CreateRecord(
                    sessionId,
                    0,
                    GatewayTrafficDirections.Inbound,
                    protocol,
                    EnvelopeContentType,
                    capturedRequest.Payload)).ConfigureAwait(false))
        {
            await WriteJournalUnavailableAsync(context, context.Response.Body, protocol).ConfigureAwait(false);
            return;
        }

        context.Request.Body = requestBody;
        try
        {
            if (capturedRequest.RejectionStatus == 0 && GatewayProtocolPaths.IsStreaming(context.Request.Path))
            {
                await CaptureStreamingResponseAsync(context, journal, options, sessionId, protocol).ConfigureAwait(false);
                return;
            }
            await CaptureBufferedResponseAsync(context, journal, options, environment, sessionId, protocol,
                capturedRequest.RejectionStatus).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Body = originalRequestBody;
        }
    }

    private async Task CaptureBufferedResponseAsync(HttpContext context, IGatewayTrafficJournal journal,
        GatewayApplicationOptions options, EnvironmentConfig environment, Guid sessionId, string protocol, int rejectionStatus)
    {
        var originalBody = context.Response.Body;
        var capturedBody = new GatewayCaptureBuffer(
            GatewayHttpPayloadBudget.MaximumBinaryBodyBytes(environment.Messaging.MaxPayloadBytes),
            StatusCodes.Status500InternalServerError);
        await using var capturedBodyLifetime = capturedBody.ConfigureAwait(false);
        context.Response.Body = capturedBody;
        try
        {
            byte[] responsePayload;
            try
            {
                if (rejectionStatus == 0)
                    await next(context).ConfigureAwait(false);
                else
                    await GatewayProtocolFailureResponse.WriteAsync(context, protocol, rejectionStatus).ConfigureAwait(false);
                SetBufferedContentLength(context, capturedBody.Length);
                responsePayload = CaptureResponse(context.Response, capturedBody.Content);
                if (responsePayload.Length > environment.Messaging.MaxPayloadBytes)
                    throw new BadHttpRequestException("The encoded HTTP response exceeds its budget.", StatusCodes.Status500InternalServerError);
            }
            // An HTTP protocol exception must be rendered and journaled within this boundary.
#pragma warning disable CA1031
            catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
#pragma warning restore CA1031
            {
                GatewayProtocolLog.CapturedHttpFailure(logger, exception, protocol, sessionId);
                var status = exception is BadHttpRequestException badRequest
                    ? badRequest.StatusCode : StatusCodes.Status500InternalServerError;
                await GatewayProtocolFailureResponse.WriteAsync(context, protocol, status).ConfigureAwait(false);
                SetBufferedContentLength(context, capturedBody.Length);
                responsePayload = CaptureResponse(context.Response, capturedBody.Content);
            }
            if (!await TryAppendAsync(journal, options,
                    CreateRecord(sessionId, 1, GatewayTrafficDirections.Outbound, protocol, EnvelopeContentType, responsePayload))
                .ConfigureAwait(false))
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
        // The streaming boundary cannot replace an already-sent status, but can fail closed.
#pragma warning disable CA1031
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
#pragma warning restore CA1031
        {
            GatewayProtocolLog.CapturedHttpFailure(logger, exception, protocol, sessionId);
            if (context.Response.HasStarted)
                context.Abort();
            else if (capture.Failed)
                await WriteJournalUnavailableAsync(context, originalBody, protocol).ConfigureAwait(false);
            else
                await GatewayProtocolFailureResponse.WriteAsync(context, protocol, StatusCodes.Status500InternalServerError)
                    .ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static long GetCaptureLimit(PathString path, string protocol, EnvironmentConfig environment) =>
        path.StartsWithSegments("/jmap/upload", StringComparison.OrdinalIgnoreCase)
            ? environment.Jmap.MaxUploadSizeBytes
            : string.Equals(protocol, "jmap", StringComparison.Ordinal)
                ? environment.Jmap.MaxRequestSizeBytes
                : string.Equals(protocol, "dav", StringComparison.Ordinal)
                    ? Math.Max(1_048_576, environment.Dav.MaxResourceSizeBytes)
                    : GatewayHttpPayloadBudget.SmallRequestBytes;

    private static async Task<RequestCapture> CaptureRequestAsync(HttpContext context,
        GatewayCaptureBuffer body, long maximumBytes, int maximumPayloadBytes)
    {
        var request = context.Request;
        long observedBytes = 0;
        try
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
                feature.MaxRequestBodySize = Math.Min(feature.MaxRequestBodySize ?? long.MaxValue, maximumBytes);
            if (request.ContentLength > maximumBytes)
                throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
            ValidateRequestMetadata(request);
            var headers = Headers(request.Headers, StatusCodes.Status431RequestHeaderFieldsTooLarge);
            var block = new byte[ReadBlockBytes];
            while (true)
            {
                var allowance = checked((int)Math.Min(block.Length, maximumBytes - body.Length + 1));
                var read = await request.Body.ReadAsync(block.AsMemory(0, allowance), context.RequestAborted).ConfigureAwait(false);
                observedBytes += read;
                if (read == 0)
                    break;
                if (observedBytes > maximumBytes)
                    throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
                await body.WriteAsync(block.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
            }
            body.Position = 0;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new HttpRequestEnvelope(
                request.Method,
                request.Path.Value ?? string.Empty,
                request.QueryString.Value ?? string.Empty,
                request.Protocol,
                request.ContentType,
                headers,
                body.Content,
                null),
                JsonOptions);
            if (payload.Length > maximumPayloadBytes)
                throw new BadHttpRequestException("The encoded HTTP request exceeds its budget.", StatusCodes.Status413PayloadTooLarge);
            return new RequestCapture(payload, 0);
        }
        // Persist bounded rejection metadata even when no request body could be captured.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            body.SetLength(0);
            body.Position = 0;
            var status = exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode : StatusCodes.Status400BadRequest;
            return new RequestCapture(JsonSerializer.SerializeToUtf8Bytes(new HttpRequestEnvelope(
                Bounded(request.Method, 32), Bounded(request.Path.Value, 1024), Bounded(request.QueryString.Value, 128),
                Bounded(request.Protocol, 32), Bounded(request.ContentType, 128),
                new Dictionary<string, string>(StringComparer.Ordinal), ReadOnlyMemory<byte>.Empty,
                new HttpRequestRejection(status, request.ContentLength, maximumBytes, observedBytes)), JsonOptions), status);
        }
    }

    private static void SetBufferedContentLength(HttpContext context, long length)
    {
        if (!HttpMethods.IsHead(context.Request.Method)
            && context.Response.StatusCode is not (StatusCodes.Status204NoContent or StatusCodes.Status304NotModified))
            context.Response.ContentLength = length;
    }

    private static byte[] CaptureResponse(HttpResponse response, ReadOnlyMemory<byte> body)
    {
        if (Encoding.UTF8.GetByteCount(response.ContentType ?? string.Empty) > 1024)
            throw new BadHttpRequestException("The response metadata exceeds its budget.", StatusCodes.Status500InternalServerError);
        return JsonSerializer.SerializeToUtf8Bytes(new HttpResponseEnvelope(
            response.StatusCode,
            response.ContentType,
            Headers(response.Headers, StatusCodes.Status500InternalServerError),
            body),
            JsonOptions);
    }

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
        // Fail closed for every journal implementation failure, including timeouts.
#pragma warning disable CA1031
        catch (Exception exception)
        {
#pragma warning restore CA1031
            GatewayProtocolLog.TrafficJournalFailure(logger, exception, record.Direction, record.Protocol);
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

    private static Dictionary<string, string> Headers(IHeaderDictionary headers, int failureStatus)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = 0L;
        foreach (var header in headers)
        {
            // Count before joining multi-valued headers, so rejected metadata is not copied.
            bytes += Encoding.UTF8.GetByteCount(header.Key) + 4;
            foreach (var value in header.Value)
                bytes += Encoding.UTF8.GetByteCount(value ?? string.Empty) + 1;
            if (bytes > GatewayHttpPayloadBudget.RequestHeadersBytes || result.Count >= 100)
                throw new BadHttpRequestException("The HTTP header metadata exceeds its budget.", failureStatus);
            result.Add(header.Key, header.Value.ToString());
        }
        return result;
    }

    private static void ValidateRequestMetadata(HttpRequest request)
    {
        var lineBytes = Encoding.UTF8.GetByteCount(request.Method)
            + Encoding.UTF8.GetByteCount(request.Path.Value ?? string.Empty)
            + Encoding.UTF8.GetByteCount(request.QueryString.Value ?? string.Empty)
            + Encoding.UTF8.GetByteCount(request.Protocol) + 4;
        if (lineBytes > GatewayHttpPayloadBudget.RequestLineBytes
            || Encoding.UTF8.GetByteCount(request.ContentType ?? string.Empty) > 1024)
            throw new BadHttpRequestException("The request metadata exceeds its budget.", StatusCodes.Status431RequestHeaderFieldsTooLarge);
    }

    private static string Bounded(string? value, int length) =>
        value is null ? string.Empty : value[..Math.Min(value.Length, length)];

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
        public bool Failed { get; private set; }

        public Task AppendStartAsync()
        {
            if (Interlocked.Exchange(ref _startRecorded, 1) != 0)
                return Task.CompletedTask;
            return AppendAsync(
                EnvelopeContentType,
                CaptureResponse(response, ReadOnlyMemory<byte>.Empty));
        }

        public async Task AppendChunkAsync(ReadOnlyMemory<byte> content)
        {
            await AppendStartAsync().ConfigureAwait(false);
            for (var offset = 0; offset < content.Length; offset += ReadBlockBytes)
                await AppendAsync(StreamChunkContentType,
                    JsonSerializer.SerializeToUtf8Bytes(new HttpStreamChunkEnvelope(
                        response.ContentType, content.Slice(offset, Math.Min(ReadBlockBytes, content.Length - offset))),
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
                Failed = true;
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
        ReadOnlyMemory<byte> BodyBase64,
        HttpRequestRejection? Rejection);

    private sealed record RequestCapture(byte[] Payload, int RejectionStatus);

    private sealed record HttpRequestRejection(int Status, long? DeclaredBytes, long MaximumBytes, long ObservedBytes);

    private sealed record HttpResponseEnvelope(
        int Status,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers,
        ReadOnlyMemory<byte> BodyBase64);

    private sealed record HttpStreamChunkEnvelope(
        string? ContentType,
        ReadOnlyMemory<byte> BodyBase64);
}
