using System.Text.Json;

namespace mk8.email.Gateway.ApplicationBridge;

public sealed class GatewayApplicationFailureMiddleware(
    RequestDelegate next,
    ILogger<GatewayApplicationFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (GatewayApplicationException exception) when (!context.Response.HasStarted)
        {
            logger.LogWarning(
                exception,
                "Application operation failed at the Gateway boundary with code {Code}",
                exception.Code);
            context.Response.Clear();
            context.Response.StatusCode = exception.IsUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;
            context.Response.Headers.CacheControl = "no-store";
            if (exception.IsUnavailable)
                context.Response.Headers.RetryAfter = "5";
            if (context.Request.Path.StartsWithSegments("/oauth"))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    new Dictionary<string, object>
                    {
                        ["error"] = exception.IsUnavailable
                            ? "temporarily_unavailable"
                            : "server_error",
                        ["error_description"] = "The application service could not complete the request.",
                    },
                    cancellationToken: context.RequestAborted);
                return;
            }

            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new Dictionary<string, object>
                {
                    ["type"] = "about:blank",
                    ["title"] = "Application service unavailable",
                    ["status"] = context.Response.StatusCode,
                },
                cancellationToken: context.RequestAborted);
        }
    }
}
