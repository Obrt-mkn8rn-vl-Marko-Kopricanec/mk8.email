using mk8.email.Configuration;
using mk8.email.Dav;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Gateway.Protocols.Dav;

internal static class GatewayDavHttpAuthentication
{
    public static Task<DavUser?> AuthenticateAsync(
        HttpContext context,
        GatewayDavStore store,
        EnvironmentConfig environment,
        CancellationToken cancellationToken) =>
        GatewayJmapAuthentication.TryParse(context.Request, environment, out var authentication)
            ? store.AuthenticateAsync(authentication, cancellationToken)
            : Task.FromResult<DavUser?>(null);

    public static async Task WriteUnauthorizedAsync(
        HttpContext context,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (environment.OAuth.EnableOAuth)
            context.Response.Headers.Append("WWW-Authenticate", "Bearer realm=\"mk8.email DAV\"");
        context.Response.Headers.Append(
            "WWW-Authenticate",
            "Basic realm=\"mk8.email DAV\", charset=\"UTF-8\"");
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Authentication required.", cancellationToken);
    }
}
