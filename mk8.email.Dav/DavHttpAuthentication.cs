using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;

namespace mk8.email.Dav;

internal static class DavHttpAuthentication
{
    public static async Task<AuthenticatedMailUser?> AuthenticateAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.Length is 0 or > 4096
            || !AuthenticationHeaderValue.TryParse(header, out var authentication)
            || string.IsNullOrEmpty(authentication.Parameter))
        {
            return null;
        }

        if (authentication.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.OAuth.EnableOAuth)
                return null;
            var tokenService = context.RequestServices.GetRequiredService<IOAuthTokenService>();
            return await tokenService.AuthenticateAccessTokenAsync(
                authentication.Parameter,
                "dav",
                cancellationToken);
        }
        if (!authentication.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            return null;

        string credentials;
        try
        {
            credentials = new UTF8Encoding(false, true).GetString(
                Convert.FromBase64String(authentication.Parameter));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return null;
        }

        var separator = credentials.IndexOf(':');
        if (separator <= 0
            || separator == credentials.Length - 1
            || credentials.ContainsAny(['\r', '\n', '\0']))
        {
            return null;
        }
        return await authenticator.AuthenticateAsync(
            credentials[..separator],
            credentials[(separator + 1)..],
            cancellationToken);
    }

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
