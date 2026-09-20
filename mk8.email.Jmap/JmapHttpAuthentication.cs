using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using mk8.email.Application.Interfaces;

namespace mk8.email.Jmap;

internal static class JmapHttpAuthentication
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
            var bytes = Convert.FromBase64String(authentication.Parameter);
            credentials = new UTF8Encoding(false, true).GetString(bytes);
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

    public static IResult Unauthorized(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"mk8.email JMAP\", charset=\"UTF-8\"";
        return Results.Json(
            new
            {
                type = "about:blank",
                title = "Authentication required",
                status = StatusCodes.Status401Unauthorized,
            },
            statusCode: StatusCodes.Status401Unauthorized,
            contentType: "application/problem+json");
    }
}
