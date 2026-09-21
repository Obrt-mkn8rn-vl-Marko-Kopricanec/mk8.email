using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using mk8.email.Application.Interfaces;

namespace mk8.email.Dav;

internal static class DavHttpAuthentication
{
    public static async Task<AuthenticatedMailUser?> AuthenticateAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.Length is 0 or > 4096
            || !AuthenticationHeaderValue.TryParse(header, out var authentication)
            || !authentication.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authentication.Parameter))
        {
            return null;
        }

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
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate =
            "Basic realm=\"mk8.email DAV\", charset=\"UTF-8\"";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Authentication required.", cancellationToken);
    }
}
