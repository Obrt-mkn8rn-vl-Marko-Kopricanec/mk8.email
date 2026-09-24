using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using NetMediaTypeHeaderValue = System.Net.Http.Headers.MediaTypeHeaderValue;

namespace mk8.email.Gateway.Protocols.Jmap;

public static class JmapEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedEventTypes = new(
        [
            "Mailbox",
            "Thread",
            "Email",
            "EmailDelivery",
            "Identity",
            "EmailSubmission",
            "VacationResponse",
            "AddressBook",
            "ContactCard",
        ],
        StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapJmapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/jmap", GetSessionAsync);
        endpoints.MapGet("/jmap/session", GetSessionAsync);
        endpoints.MapPost("/jmap/api", ProcessRequestAsync);
        endpoints.MapPost("/jmap/upload/{accountId}", UploadAsync);
        endpoints.MapGet("/jmap/download/{accountId}/{blobId}/{name}", DownloadAsync);
        endpoints.MapGet("/jmap/event", EventSourceAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        IGatewayJmapClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication))
            return GatewayJmapAuthentication.Unauthorized(context, environment);

        var result = await application.GetSessionAsync(
            new JmapSessionApplicationRequest(authentication),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal))
            return GatewayJmapAuthentication.Unauthorized(context, environment);

        SetJmapResponseHeaders(context.Response);
        return Result(result);
    }

    private static async Task<IResult> ProcessRequestAsync(
        HttpContext context,
        IGatewayJmapClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication))
            return GatewayJmapAuthentication.Unauthorized(context, environment);
        SetJmapResponseHeaders(context.Response);
        if (!HasJmapJsonContentType(context.Request))
        {
            return Problem(new JmapApplicationProblem(
                "urn:ietf:params:jmap:error:notJSON",
                "Unsupported media type",
                "JMAP API requests must use the application/json media type."),
                StatusCodes.Status415UnsupportedMediaType);
        }

        byte[] document;
        try
        {
            document = await ReadBodyAsync(
                context.Request,
                environment.Jmap.MaxRequestSizeBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayJmapBodyLimitException)
        {
            return Problem(new JmapApplicationProblem(
                "urn:ietf:params:jmap:error:limit",
                "Request limit exceeded",
                "The JMAP request is larger than the server limit.",
                "maxSizeRequest"));
        }

        var result = await application.ProcessApiRequestAsync(
            new JmapApiApplicationRequest(authentication, document),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal))
            return GatewayJmapAuthentication.Unauthorized(context, environment);
        return Result(result);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        string accountId,
        IGatewayJmapClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication))
            return GatewayJmapAuthentication.Unauthorized(context, environment);

        byte[] content;
        try
        {
            content = await ReadBodyAsync(
                context.Request,
                environment.Jmap.MaxUploadSizeBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayJmapBodyLimitException)
        {
            return UploadProblem(
                StatusCodes.Status413PayloadTooLarge,
                "Upload is larger than the server limit.");
        }

        var contentType = NormalizeMediaType(context.Request.ContentType);
        var result = await application.UploadAsync(
            new JmapUploadApplicationRequest(
                authentication,
                accountId,
                contentType,
                content),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal))
            return GatewayJmapAuthentication.Unauthorized(context, environment);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.NotFound, StringComparison.Ordinal))
            return ResourceNotFound("The account or upload resource was not found.");
        if (result.Problem is not null)
            return Problem(result.Problem);
        if (result.BlobId is null || result.Size is null)
            throw new InvalidOperationException("The Application returned an incomplete JMAP upload result.");

        SetJmapResponseHeaders(context.Response);
        return Results.Json(new JsonObject
        {
            ["accountId"] = accountId,
            ["blobId"] = result.BlobId,
            ["type"] = result.ContentType ?? contentType,
            ["size"] = result.Size.Value,
        }, JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        string accountId,
        string blobId,
        string name,
        IGatewayJmapClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication))
            return GatewayJmapAuthentication.Unauthorized(context, environment);

        var result = await application.DownloadAsync(
            new JmapDownloadApplicationRequest(authentication, accountId, blobId),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal))
            return GatewayJmapAuthentication.Unauthorized(context, environment);
        if (string.Equals(result.Outcome, JmapApplicationOutcomes.NotFound, StringComparison.Ordinal) || result.Content is null)
            return ResourceNotFound("The account or blob was not found.");

        var requestedType = NormalizeMediaType(context.Request.Query["accept"].ToString());
        context.Response.Headers.CacheControl = "private, immutable, max-age=31536000";
        return CreateDownloadResult(
            result.Content,
            requestedType,
            name);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The durable presentation boundary keeps its ordered validation, journaling and failure handling together.")]
    private static async Task EventSourceAsync(
        HttpContext context,
        IGatewayJmapClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication))
        {
            await GatewayJmapAuthentication.Unauthorized(context, environment).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (!TryEventTypes(context.Request.Query["types"].ToString(), out var types)
            || context.Request.Query["closeafter"].ToString() is not ("state" or "no")
            || !TryNormalizeEventSourcePing(context.Request.Query["ping"].ToString(), out var ping))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new JsonObject
            {
                ["type"] = "about:blank",
                ["title"] = "Invalid event source parameters",
                ["status"] = StatusCodes.Status400BadRequest,
            }, JsonOptions, cancellationToken).ConfigureAwait(false);
            return;
        }

        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
        long? cursor = null;
        if (!string.IsNullOrEmpty(lastEventId))
        {
            cursor = lastEventId.Length > 1
                && lastEventId[0] == 'c'
                && long.TryParse(
                    lastEventId.AsSpan(1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedCursor)
                && parsedCursor >= 0
                    ? parsedCursor
                    : -1;
        }

        var initial = await application.PollEventAsync(
            new JmapEventApplicationRequest(authentication, cursor, types),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(initial.Outcome, JmapApplicationOutcomes.Unauthorized, StringComparison.Ordinal))
        {
            await GatewayJmapAuthentication.Unauthorized(context, environment).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (initial.Problem is not null || initial.Cursor is null)
        {
            await Problem(initial.Problem ?? new JmapApplicationProblem(
                "about:blank",
                "Invalid event source state")).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        cursor = initial.Cursor.Value;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        var closeAfterState = context.Request.Query["closeafter"] == "state";
        var lastSent = DateTime.UtcNow;
        try
        {
            if (initial.Content is not null)
            {
                await WriteStateEventAsync(context.Response, cursor.Value, initial.Content, cancellationToken).ConfigureAwait(false);
                if (closeAfterState)
                    return;
                lastSent = DateTime.UtcNow;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var poll = await application.PollEventAsync(
                    new JmapEventApplicationRequest(authentication, cursor, types),
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(poll.Outcome, JmapApplicationOutcomes.Ok, StringComparison.Ordinal) || poll.Cursor is null)
                    return;
                cursor = poll.Cursor.Value;
                if (poll.Content is not null)
                {
                    await WriteStateEventAsync(context.Response, cursor.Value, poll.Content, cancellationToken).ConfigureAwait(false);
                    lastSent = DateTime.UtcNow;
                    if (closeAfterState)
                        return;
                }
                else if (ping > 0 && DateTime.UtcNow - lastSent >= TimeSpan.FromSeconds(ping))
                {
                    await context.Response.WriteAsync(
                        $"event: ping\ndata: {{\"interval\":{ping}}}\n\n",
                        cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastSent = DateTime.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task WriteStateEventAsync(
        HttpResponse response,
        long cursor,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetString(content);
        await response.WriteAsync(
            $"id: c{cursor}\nevent: state\ndata: {data}\n\n",
            cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool TryEventTypes(string value, out string[]? types)
    {
        types = null;
        if (string.Equals(value, "*", StringComparison.Ordinal))
            return true;
        if (string.IsNullOrEmpty(value))
            return false;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in value.Split(','))
        {
            if (!SupportedEventTypes.Contains(type))
                return false;
            result.Add(type);
        }
        types = result.ToArray();
        return true;
    }

    internal static bool TryNormalizeEventSourcePing(string value, out int ping)
    {
        ping = 0;
        if (value.Length == 0)
            return false;
        foreach (var character in value)
        {
            var digit = character - '0';
            if ((uint)digit > 9)
                return false;
            if (ping <= 300)
                ping = (ping * 10) + digit;
        }
        ping = ping == 0 ? 0 : Math.Clamp(ping, 15, 300);
        return true;
    }

    internal static bool HasJmapJsonContentType(HttpRequest request)
    {
        if (!NetMediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
            return false;
        return string.Equals(
            contentType.MediaType,
            "application/json",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeMediaType(string? value)
    {
        if (!NetMediaTypeHeaderValue.TryParse(value, out var parsed)
            || !TryNormalizeRestrictedMediaType(parsed.MediaType, out var normalized))
        {
            return "application/octet-stream";
        }
        return normalized;
    }

    private static bool TryNormalizeRestrictedMediaType(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
            return false;
        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0
            || separator != value.LastIndexOf('/')
            || !IsRestrictedName(value.AsSpan(0, separator))
            || !IsRestrictedName(value.AsSpan(separator + 1)))
        {
            return false;
        }
        // MIME media types are conventionally normalized to lower-case ASCII on the wire.
#pragma warning disable CA1308
        normalized = value.ToLowerInvariant();
#pragma warning restore CA1308
        return true;
    }

    private static bool IsRestrictedName(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 127 || !IsAsciiLetterOrDigit(value[0]))
            return false;
        foreach (ref readonly var character in value[1..])
        {
            if (!IsAsciiLetterOrDigit(character)
                && character is not ('!' or '#' or '$' or '&' or '-' or '^' or '_' or '.' or '+'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
            throw new GatewayJmapBodyLimitException("The JMAP request body exceeds the configured limit.");
        var buffer = new MemoryStream();
        await using var bufferLifetime = buffer.ConfigureAwait(false);
        var block = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new GatewayJmapBodyLimitException("The JMAP request body exceeds the configured limit.");
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static IResult Result(JmapApplicationResult result)
    {
        if (result.Problem is not null)
            return Problem(result.Problem);
        if (result.Content is null)
            throw new InvalidOperationException("The Application returned an incomplete JMAP result.");
        return Results.Bytes(result.Content, result.ContentType ?? "application/json");
    }

    private static IResult Problem(
        JmapApplicationProblem problem,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        var value = new JsonObject
        {
            ["type"] = problem.Type,
            ["title"] = problem.Title,
            ["status"] = statusCode,
        };
        if (!string.IsNullOrEmpty(problem.Detail))
            value["detail"] = problem.Detail;
        if (problem.Limit is not null)
            value["limit"] = problem.Limit;
        return Results.Json(
            value,
            JsonOptions,
            statusCode: statusCode,
            contentType: "application/problem+json");
    }

    private static IResult UploadProblem(int status, string detail) => Results.Json(
        new JsonObject
        {
            ["type"] = "about:blank",
            ["title"] = "Upload failed",
            ["status"] = status,
            ["detail"] = detail,
        },
        JsonOptions,
        statusCode: status,
        contentType: "application/problem+json");

    private static IResult ResourceNotFound(string detail) => Results.Json(
        new JsonObject
        {
            ["type"] = "about:blank",
            ["title"] = "Resource not found",
            ["status"] = StatusCodes.Status404NotFound,
            ["detail"] = detail,
        },
        JsonOptions,
        statusCode: StatusCodes.Status404NotFound,
        contentType: "application/problem+json");

    internal static IResult CreateDownloadResult(
        byte[] content,
        string requestedType,
        string name) =>
        Results.File(
            content,
            requestedType,
            name,
            enableRangeProcessing: true);

    private static void SetJmapResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.ContentLanguage = "en";
        response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderNames.Authorization);
    }

    private sealed class GatewayJmapBodyLimitException : Exception
    {
        public GatewayJmapBodyLimitException()
        {
        }

        public GatewayJmapBodyLimitException(string message) : base(message)
        {
        }

        public GatewayJmapBodyLimitException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
