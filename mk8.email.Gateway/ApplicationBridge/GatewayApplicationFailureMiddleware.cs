using System.Text.Json;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Autodiscover;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Gateway.ApplicationBridge;

public sealed class GatewayApplicationFailureMiddleware(
    RequestDelegate next,
    ILogger<GatewayApplicationFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (GatewayApplicationException exception) when (!context.Response.HasStarted)
        {
            GatewayApplicationLog.OperationFailed(logger, exception, exception.Code);
            context.Response.Clear();
            context.Response.StatusCode = exception.IsUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;
            context.Response.Headers.CacheControl = "no-store";
            if (exception.IsUnavailable)
                context.Response.Headers.RetryAfter = "5";
            if (GatewayProtocolPaths.IsEws(context.Request.Path))
            {
                await GatewayEwsSoap.WriteAsync(context, context.Response.Body,
                    GatewayEwsSoap.Fault(exception.IsUnavailable ? "ErrorServerBusy" : "ErrorInternalServerError",
                        "The application service could not complete the request."), context.Response.StatusCode).ConfigureAwait(false);
                return;
            }
            if (GatewayProtocolPaths.IsAutodiscover(context.Request.Path))
            {
                await GatewayAutodiscoverXml.WriteAsync(context, context.Response.Body,
                    GatewayAutodiscoverXml.Error(603, "The application service could not complete the request."),
                    context.Response.StatusCode).ConfigureAwait(false);
                return;
            }
            if (GatewayProtocolPaths.IsOAuth(context.Request.Path))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["error"] = exception.IsUnavailable
                            ? "temporarily_unavailable"
                            : "server_error",
                        ["error_description"] = "The application service could not complete the request.",
                    },
                    cancellationToken: context.RequestAborted).ConfigureAwait(false);
                return;
            }

            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "about:blank",
                    ["title"] = "Application service unavailable",
                    ["status"] = context.Response.StatusCode,
                },
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }
}
