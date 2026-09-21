using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.OAuth;

public static class OAuthEndpointRouteBuilderExtensions
{
    private const string CsrfCookieName = "__Host-mk8oauth";
    private const int MaximumFormBytes = 16 * 1024;
    private static readonly string[] AdvertisedScopes =
        ["offline_access", "imap", "smtp", "pop", "jmap", "dav", "sieve"];

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/oauth-authorization-server", MetadataAsync);
        endpoints.MapGet("/oauth/authorize", BeginAuthorizationAsync);
        endpoints.MapPost("/oauth/authorize", CompleteAuthorizationAsync)
            .RequireRateLimiting("oauth-authorize");
        endpoints.MapPost("/oauth/token", ExchangeTokenAsync)
            .RequireRateLimiting("oauth-token");
        endpoints.MapPost("/oauth/revoke", RevokeTokenAsync)
            .RequireRateLimiting("oauth-token");
        return endpoints;
    }

    private static IResult MetadataAsync(HttpContext context, EnvironmentConfig environment)
    {
        var baseUri = environment.OAuth.GetPublicBaseUri(
            environment.Smtp.Hostname,
            environment.Jmap.PublicBaseUrl);
        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = baseUri.AbsoluteUri.TrimEnd('/'),
            ["authorization_endpoint"] = new Uri(baseUri, "oauth/authorize").AbsoluteUri,
            ["token_endpoint"] = new Uri(baseUri, "oauth/token").AbsoluteUri,
            ["revocation_endpoint"] = new Uri(baseUri, "oauth/revoke").AbsoluteUri,
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["scopes_supported"] = AdvertisedScopes,
        });
    }

    private static async Task BeginAuthorizationAsync(
        HttpContext context,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        var parsed = TryParseAuthorizationRequest(
            name => context.Request.Query[name].ToString(),
            environment,
            out var request,
            out var error);
        if (!parsed)
        {
            await WriteAuthorizationErrorAsync(context, environment, error, cancellationToken);
            return;
        }

        var csrf = CreateRandomValue();
        context.Response.Cookies.Append(CsrfCookieName, csrf, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/",
            IsEssential = true,
        });
        await WriteLoginPageAsync(context, request!, csrf, null, cancellationToken);
    }

    private static async Task CompleteAuthorizationAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        IMfaService mfaService,
        IOAuthAuthorizationService authorizationService,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType
            || context.Request.ContentLength is > MaximumFormBytes)
        {
            await WritePlainErrorAsync(context, StatusCodes.Status400BadRequest,
                "The authorization form is not valid.", cancellationToken);
            return;
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var parsed = TryParseAuthorizationRequest(
            name => form[name].ToString(),
            environment,
            out var request,
            out var error);
        if (!parsed)
        {
            await WriteAuthorizationErrorAsync(context, environment, error, cancellationToken);
            return;
        }

        var csrf = form["csrf"].ToString();
        var cookie = context.Request.Cookies[CsrfCookieName] ?? string.Empty;
        if (!FixedTimeTextEquals(csrf, cookie) || csrf.Length < 32)
        {
            await WritePlainErrorAsync(context, StatusCodes.Status400BadRequest,
                "The authorization form expired. Start the sign-in again.", cancellationToken);
            return;
        }

        var username = form["username"].ToString();
        var password = form["password"].ToString();
        var mfaCode = form["mfa_code"].ToString();
        var deviceName = form["device_name"].ToString().Trim();
        if (username.Length > 320 || password.Length > 1024
            || mfaCode.Length > 128
            || deviceName.Length is < 1 or > 128)
        {
            await WriteLoginPageAsync(
                context,
                request!,
                csrf,
                "The email address, password, verification code, or device name is not valid.",
                cancellationToken,
                StatusCodes.Status400BadRequest);
            return;
        }

        var user = await authenticator.AuthenticatePrimaryAsync(
            username,
            password,
            cancellationToken);
        if (user is null)
        {
            await WriteLoginPageAsync(
                context,
                request!,
                csrf,
                "The email address or password is not valid.",
                cancellationToken,
                StatusCodes.Status401Unauthorized);
            return;
        }

        var mfaResult = await mfaService.VerifyForAuthenticationAsync(
            user.Id,
            mfaCode,
            cancellationToken);
        if (mfaResult == MfaVerificationResult.Failed)
        {
            await WriteLoginPageAsync(
                context,
                request!,
                csrf,
                "The email address, password, or verification code is not valid.",
                cancellationToken,
                StatusCodes.Status401Unauthorized);
            return;
        }

        var code = await authorizationService.CreateAuthorizationCodeAsync(
            user.Id,
            request!.ClientId,
            request.RedirectUri,
            deviceName,
            request.Scopes,
            request.CodeChallenge,
            cancellationToken);
        if (code is null)
        {
            await WritePlainErrorAsync(context, StatusCodes.Status400BadRequest,
                "The authorization could not be created.", cancellationToken);
            return;
        }

        context.Response.Cookies.Delete(CsrfCookieName, new CookieOptions
        {
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
        context.Response.Redirect(BuildRedirectUri(
            request.RedirectUri,
            [("code", code), ("state", request.State)]));
    }

    private static async Task<IResult> ExchangeTokenAsync(
        HttpContext context,
        IOAuthAuthorizationService authorizationService,
        IOAuthTokenService tokenService,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        if (!context.Request.HasFormContentType
            || context.Request.ContentLength is > MaximumFormBytes)
        {
            return OAuthError("invalid_request", "Use a bounded form-encoded request.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var clientId = form["client_id"].ToString();
        if (!string.Equals(clientId, environment.OAuth.ClientId, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(form["client_secret"].ToString()))
        {
            return OAuthError("invalid_client", "The public client identifier is not valid.",
                StatusCodes.Status401Unauthorized);
        }

        OAuthTokenPair? pair;
        switch (form["grant_type"].ToString())
        {
            case "authorization_code":
                pair = await authorizationService.RedeemAuthorizationCodeAsync(
                    form["code"].ToString(),
                    clientId,
                    form["redirect_uri"].ToString(),
                    form["code_verifier"].ToString(),
                    cancellationToken);
                break;
            case "refresh_token":
                pair = await tokenService.RefreshAsync(
                    form["refresh_token"].ToString(),
                    clientId,
                    cancellationToken);
                break;
            default:
                return OAuthError("unsupported_grant_type", "The grant type is not supported.");
        }

        return pair is null
            ? OAuthError("invalid_grant", "The authorization grant is invalid or expired.")
            : Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = pair.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = pair.ExpiresInSeconds,
                ["refresh_token"] = pair.RefreshToken,
                ["scope"] = pair.Scope,
            });
    }

    private static async Task<IResult> RevokeTokenAsync(
        HttpContext context,
        IOAuthTokenService tokenService,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        if (!context.Request.HasFormContentType
            || context.Request.ContentLength is > MaximumFormBytes)
        {
            return OAuthError("invalid_request", "Use a bounded form-encoded request.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var clientId = form["client_id"].ToString();
        if (!string.Equals(clientId, environment.OAuth.ClientId, StringComparison.Ordinal))
            return OAuthError("invalid_client", "The public client identifier is not valid.",
                StatusCodes.Status401Unauthorized);

        await tokenService.RevokeTokenAsync(
            form["token"].ToString(),
            clientId,
            cancellationToken);
        return Results.Ok();
    }

    internal static bool TryParseAuthorizationRequest(
        Func<string, string> getValue,
        EnvironmentConfig environment,
        out OAuthAuthorizationRequest? request,
        out OAuthAuthorizationError error)
    {
        request = null;
        var clientId = getValue("client_id");
        var redirectUri = getValue("redirect_uri");
        var state = getValue("state");
        error = new(
            "invalid_request",
            "The authorization request is not valid.",
            clientId,
            redirectUri,
            state);
        if (!string.Equals(clientId, environment.OAuth.ClientId, StringComparison.Ordinal))
        {
            error = error with { Code = "unauthorized_client", Description = "The client is not registered." };
            return false;
        }
        if (!OAuthProtocolValues.IsAllowedRedirectUri(redirectUri))
        {
            error = error with { Description = "The redirect URI is not allowed." };
            return false;
        }
        if (getValue("response_type") != "code")
        {
            error = error with { Code = "unsupported_response_type", Description = "Only authorization code responses are supported." };
            return false;
        }
        if (state.Length is < 16 or > 1024 || state.Any(char.IsControl))
        {
            error = error with { Description = "A bounded state value is required." };
            return false;
        }
        if (getValue("code_challenge_method") != "S256"
            || !OAuthProtocolValues.IsValidPkceChallenge(getValue("code_challenge")))
        {
            error = error with { Description = "PKCE with the S256 method is required." };
            return false;
        }
        if (!OAuthProtocolValues.TryNormalizeScopes(
                [getValue("scope")],
                out var scopes)
            || !scopes.Any(scope => scope is "imap" or "smtp" or "pop" or "jmap" or "dav" or "sieve"))
        {
            error = error with { Code = "invalid_scope", Description = "The requested scopes are not valid." };
            return false;
        }

        var loginHint = getValue("login_hint");
        if (loginHint.Length > 320 || loginHint.Any(char.IsControl))
        {
            error = error with { Description = "The login hint is not valid." };
            return false;
        }
        request = new(
            clientId,
            redirectUri,
            state,
            scopes,
            getValue("code_challenge"),
            loginHint);
        return true;
    }

    private static async Task WriteAuthorizationErrorAsync(
        HttpContext context,
        EnvironmentConfig environment,
        OAuthAuthorizationError error,
        CancellationToken cancellationToken)
    {
        if (string.Equals(error.RedirectUri, error.RedirectUri?.Trim(), StringComparison.Ordinal)
            && string.Equals(environment.OAuth.ClientId, error.ClientId, StringComparison.Ordinal)
            && OAuthProtocolValues.IsAllowedRedirectUri(error.RedirectUri ?? string.Empty)
            && error.State.Length is >= 16 and <= 1024
            && !error.State.Any(char.IsControl))
        {
            context.Response.Redirect(BuildRedirectUri(
                error.RedirectUri!,
                [("error", error.Code), ("error_description", error.Description), ("state", error.State)]));
            return;
        }

        await WritePlainErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            error.Description,
            cancellationToken);
    }

    private static async Task WriteLoginPageAsync(
        HttpContext context,
        OAuthAuthorizationRequest request,
        string csrf,
        string? error,
        CancellationToken cancellationToken,
        int statusCode = StatusCodes.Status200OK)
    {
        SetSensitiveResponseHeaders(context.Response);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        Func<string, string> encode = HtmlEncoder.Default.Encode;
        var errorMarkup = error is null
            ? string.Empty
            : $"<p role=\"alert\">{encode(error)}</p>";
        var html = $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Authorize Thunderbird</title></head>
            <body><main>
            <h1>Connect Thunderbird to mk8.email</h1>
            <p>Sign in to authorize this device for: {encode(string.Join(", ", request.Scopes))}.</p>
            {errorMarkup}
            <form method="post" action="/oauth/authorize">
            {Hidden("response_type", "code")}
            {Hidden("client_id", request.ClientId)}
            {Hidden("redirect_uri", request.RedirectUri)}
            {Hidden("scope", string.Join(' ', request.Scopes))}
            {Hidden("state", request.State)}
            {Hidden("code_challenge", request.CodeChallenge)}
            {Hidden("code_challenge_method", "S256")}
            {Hidden("csrf", csrf)}
            <p><label>Email address <input name="username" type="email" autocomplete="username" value="{encode(request.LoginHint)}" maxlength="320" required></label></p>
            <p><label>Password <input name="password" type="password" autocomplete="current-password" maxlength="1024" required></label></p>
            <p><label>Authenticator or recovery code (if enabled) <input name="mfa_code" autocomplete="one-time-code" maxlength="128"></label></p>
            <p><label>Device name <input name="device_name" value="Thunderbird" maxlength="128" required></label></p>
            <button type="submit">Authorize</button>
            </form>
            </main></body></html>
            """;
        await context.Response.WriteAsync(html, cancellationToken);

        string Hidden(string name, string value) =>
            $"<input type=\"hidden\" name=\"{name}\" value=\"{encode(value)}\">";
    }

    private static IResult OAuthError(string code, string description, int statusCode = 400) =>
        Results.Json(
            new Dictionary<string, object>
            {
                ["error"] = code,
                ["error_description"] = description,
            },
            statusCode: statusCode);

    private static async Task WritePlainErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, cancellationToken);
    }

    private static void SetSensitiveResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static string CreateRandomValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string BuildRedirectUri(
        string redirectUri,
        IEnumerable<(string Name, string Value)> values)
    {
        var query = string.Join('&', values.Select(value =>
            $"{WebUtility.UrlEncode(value.Name)}={WebUtility.UrlEncode(value.Value)}"));
        return redirectUri + "?" + query;
    }

    internal sealed record OAuthAuthorizationRequest(
        string ClientId,
        string RedirectUri,
        string State,
        string[] Scopes,
        string CodeChallenge,
        string LoginHint);

    internal sealed record OAuthAuthorizationError(
        string Code,
        string Description,
        string ClientId,
        string? RedirectUri,
        string State);
}
