using System.Net.Http.Headers;
using System.Text;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapAuthentication
{
    public static bool TryParse(
        HttpRequest request,
        EnvironmentConfig environment,
        out ProtocolAuthentication authentication)
    {
        authentication = null!;
        var header = request.Headers.Authorization.ToString();
        if (header.Length is 0 or > 4096
            || !AuthenticationHeaderValue.TryParse(header, out var parsed)
            || string.IsNullOrEmpty(parsed.Parameter))
        {
            return false;
        }

        if (parsed.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.OAuth.EnableOAuth
                || parsed.Parameter.ContainsAny(['\r', '\n', '\0']))
            {
                return false;
            }
            authentication = new ProtocolAuthentication(
                ProtocolAuthenticationKinds.BearerToken,
                null,
                parsed.Parameter);
            return true;
        }
        if (!parsed.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            return false;

        string credentials;
        try
        {
            credentials = new UTF8Encoding(false, true).GetString(
                Convert.FromBase64String(parsed.Parameter));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }

        var separator = credentials.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || separator == credentials.Length - 1
            || credentials.ContainsAny(['\r', '\n', '\0']))
        {
            return false;
        }

        authentication = new ProtocolAuthentication(
            ProtocolAuthenticationKinds.Password,
            credentials[..separator],
            credentials[(separator + 1)..]);
        return true;
    }

    public static IResult Unauthorized(HttpContext context, EnvironmentConfig environment)
    {
        if (environment.OAuth.EnableOAuth)
            context.Response.Headers.Append("WWW-Authenticate", "Bearer realm=\"mk8.email JMAP\"");
        context.Response.Headers.Append(
            "WWW-Authenticate",
            "Basic realm=\"mk8.email JMAP\", charset=\"UTF-8\"");
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
