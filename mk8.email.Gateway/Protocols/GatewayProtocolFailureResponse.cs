using System.Text.Json;
using mk8.email.Gateway.Protocols.Autodiscover;

namespace mk8.email.Gateway.Protocols;

internal static class GatewayProtocolFailureResponse
{
    public static async Task WriteAsync(HttpContext context, string protocol, int status)
    {
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        if (string.Equals(protocol, "autodiscover", StringComparison.Ordinal))
        {
            await GatewayAutodiscoverXml.WriteAsync(context, context.Response.Body,
                GatewayAutodiscoverXml.Error(status < 500 ? 600 : 603,
                    status == StatusCodes.Status413PayloadTooLarge
                        ? "The request body exceeds the server limit." : "The gateway could not complete the request."), status)
                .ConfigureAwait(false);
            return;
        }
        if (string.Equals(protocol, "oauth", StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["error"] = status < 500 ? "invalid_request" : "server_error",
                    ["error_description"] = status == StatusCodes.Status413PayloadTooLarge
                        ? "The request body exceeds the server limit."
                        : "The gateway could not complete the request.",
                }, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/problem+json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "about:blank",
                ["title"] = status == StatusCodes.Status413PayloadTooLarge
                    ? "Request body too large" : status < 500 ? "Request rejected" : "Gateway processing failure",
                ["status"] = status,
            }, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }
}
