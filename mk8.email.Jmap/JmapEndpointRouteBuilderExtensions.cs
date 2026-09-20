using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

public static class JmapEndpointRouteBuilderExtensions
{
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

    private static async Task EventSourceAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        JmapStateChangeService stateChanges,
        CancellationToken cancellationToken)
    {
        var user = await JmapHttpAuthentication.AuthenticateAsync(context, authenticator, cancellationToken);
        if (user is null)
        {
            await JmapHttpAuthentication.Unauthorized(context).ExecuteAsync(context);
            return;
        }
        if (!TryEventTypes(context.Request.Query["types"].ToString(), out var types)
            || context.Request.Query["closeafter"].ToString() is not ("state" or "no")
            || !TryNormalizeEventSourcePing(
                context.Request.Query["ping"].ToString(),
                out var ping))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new JsonObject
            {
                ["type"] = "about:blank",
                ["title"] = "Invalid event source parameters",
                ["status"] = StatusCodes.Status400BadRequest,
            }, JmapJson.SerializerOptions, cancellationToken);
            return;
        }

        var closeAfterState = context.Request.Query["closeafter"] == "state";
        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
        long cursor;
        if (string.IsNullOrEmpty(lastEventId))
            cursor = await stateChanges.GetCursorAsync(user, cancellationToken);
        else if (lastEventId.Length > 1
            && lastEventId[0] == 'c'
            && long.TryParse(
                lastEventId.AsSpan(1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedCursor)
            && parsedCursor >= 0)
            cursor = parsedCursor;
        else
            cursor = -1;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(cancellationToken);
        var lastSent = DateTime.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var poll = await stateChanges.PollAsync(user, cursor, types, cancellationToken);
                cursor = poll.Cursor;
                if (poll.StateChange is not null)
                {
                    await context.Response.WriteAsync(
                        $"id: c{cursor}\nevent: state\ndata: {poll.StateChange.ToJsonString(JmapJson.SerializerOptions)}\n\n",
                        cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    lastSent = DateTime.UtcNow;
                    if (closeAfterState)
                        return;
                }
                else if (ping > 0 && DateTime.UtcNow - lastSent >= TimeSpan.FromSeconds(ping))
                {
                    await context.Response.WriteAsync(
                        $"event: ping\ndata: {{\"interval\":{ping}}}\n\n",
                        cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    lastSent = DateTime.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool TryEventTypes(string value, out IReadOnlySet<string>? types)
    {
        types = null;
        if (value == "*")
            return true;
        if (string.IsNullOrEmpty(value))
            return false;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in value.Split(','))
        {
            if (!JmapStateChangeService.SupportedTypes.Contains(type) || !result.Add(type))
                return false;
        }
        types = result;
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

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        string accountId,
        IMailAuthenticator authenticator,
        JmapAccountService accounts,
        JmapBlobService blobs,
        JmapConcurrencyLimiter limiter,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        var user = await JmapHttpAuthentication.AuthenticateAsync(context, authenticator, cancellationToken);
        if (user is null)
            return JmapHttpAuthentication.Unauthorized(context);
        var account = await accounts.GetAccountAsync(user, accountId, cancellationToken);
        if (account is null)
            return ResourceNotFound("The account or upload resource was not found.");
        if (context.Request.ContentLength > environment.Jmap.MaxUploadSizeBytes)
            return UploadProblem(StatusCodes.Status413PayloadTooLarge, "Upload is larger than the server limit.");

        IDisposable uploadLease;
        try
        {
            uploadLease = await limiter.AcquireUploadAsync(cancellationToken);
        }
        catch (JmapRequestException exception)
        {
            return Problem(exception);
        }
        using (uploadLease)
        {

            await using var buffer = new MemoryStream();
            var block = new byte[16 * 1024];
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(block, cancellationToken);
                if (read == 0)
                    break;
                if (buffer.Length + read > environment.Jmap.MaxUploadSizeBytes)
                    return UploadProblem(StatusCodes.Status413PayloadTooLarge, "Upload is larger than the server limit.");
                await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
            }

            var contentType = NormalizeMediaType(context.Request.ContentType);
            var stored = await blobs.StoreAsync(
                account.InboxId,
                buffer.ToArray(),
                contentType,
                null,
                cancellationToken);
            SetJmapResponseHeaders(context.Response);
            return Results.Json(new JsonObject
            {
                ["accountId"] = accountId,
                ["blobId"] = stored.BlobId,
                ["type"] = contentType,
                ["size"] = stored.SizeBytes,
            }, JmapJson.SerializerOptions, statusCode: StatusCodes.Status201Created);
        }
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        string accountId,
        string blobId,
        string name,
        IMailAuthenticator authenticator,
        JmapAccountService accounts,
        JmapBlobService blobs,
        CancellationToken cancellationToken)
    {
        var user = await JmapHttpAuthentication.AuthenticateAsync(context, authenticator, cancellationToken);
        if (user is null)
            return JmapHttpAuthentication.Unauthorized(context);
        var account = await accounts.GetAccountAsync(user, accountId, cancellationToken);
        if (account is null)
            return ResourceNotFound("The account or blob was not found.");
        var blob = await blobs.GetAsync(account.InboxId, blobId, cancellationToken);
        if (blob is null)
            return ResourceNotFound("The account or blob was not found.");

        var requestedType = NormalizeMediaType(context.Request.Query["accept"].ToString());
        context.Response.Headers.CacheControl = "private, immutable, max-age=31536000";
        return CreateDownloadResult(blob.Content, requestedType, name);
    }

    internal static IResult CreateDownloadResult(
        byte[] content,
        string requestedType,
        string name) =>
        Results.File(
            content,
            requestedType,
            name,
            enableRangeProcessing: true);

    private static IResult UploadProblem(int status, string detail) => Results.Json(
        new JsonObject
        {
            ["type"] = "about:blank",
            ["title"] = "Upload failed",
            ["status"] = status,
            ["detail"] = detail,
        },
        JmapJson.SerializerOptions,
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
        JmapJson.SerializerOptions,
        statusCode: StatusCodes.Status404NotFound,
        contentType: "application/problem+json");

    internal static string NormalizeMediaType(string? value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var parsed))
            return "application/octet-stream";

        return JmapMediaType.TryNormalize(parsed.MediaType.Value, out var normalized)
            ? normalized
            : "application/octet-stream";
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        JmapSessionService sessions,
        CancellationToken cancellationToken)
    {
        var user = await JmapHttpAuthentication.AuthenticateAsync(
            context,
            authenticator,
            cancellationToken);
        if (user is null)
            return JmapHttpAuthentication.Unauthorized(context);

        SetJmapResponseHeaders(context.Response);
        var session = await sessions.BuildAsync(user, cancellationToken);
        return Results.Json(
            session.Value,
            JmapJson.SerializerOptions,
            contentType: "application/json");
    }

    private static async Task<IResult> ProcessRequestAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        JmapRequestProcessor processor,
        JmapConcurrencyLimiter limiter,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        var user = await JmapHttpAuthentication.AuthenticateAsync(
            context,
            authenticator,
            cancellationToken);
        if (user is null)
            return JmapHttpAuthentication.Unauthorized(context);

        SetJmapResponseHeaders(context.Response);
        if (!HasJmapJsonContentType(context.Request))
        {
            return Problem(new JmapRequestException(
                "urn:ietf:params:jmap:error:notJSON",
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type",
                "JMAP API requests must use the application/json media type."));
        }

        try
        {
            using var requestLease = await limiter.AcquireRequestAsync(cancellationToken);
            var request = await JmapJson.ParseRequestAsync(
                context.Request,
                environment.Jmap,
                cancellationToken);
            var response = await processor.ProcessAsync(request, user, cancellationToken);
            return Results.Json(
                response,
                JmapJson.SerializerOptions,
                contentType: "application/json");
        }
        catch (JmapRequestException exception)
        {
            return Problem(exception);
        }
    }

    private static IResult Problem(JmapRequestException exception)
    {
        var value = new JsonObject
        {
            ["type"] = exception.Type,
            ["title"] = exception.Title,
            ["status"] = exception.StatusCode,
        };
        if (!string.IsNullOrEmpty(exception.Detail))
            value["detail"] = exception.Detail;
        if (exception.Limit is not null)
            value["limit"] = exception.Limit;

        return Results.Json(
            value,
            JmapJson.SerializerOptions,
            statusCode: exception.StatusCode,
            contentType: "application/problem+json");
    }

    private static void SetJmapResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.ContentLanguage = "en";
        response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderNames.Authorization);
    }

    internal static bool HasJmapJsonContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
            return false;
        return string.Equals(
            contentType.MediaType.Value,
            "application/json",
            StringComparison.OrdinalIgnoreCase);
    }
}
