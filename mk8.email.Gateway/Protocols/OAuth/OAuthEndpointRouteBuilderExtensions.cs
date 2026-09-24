using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Protocol;

namespace mk8.email.Gateway.Protocols.OAuth;

public static class OAuthEndpointRouteBuilderExtensions
{
    private const string CsrfCookieName = "__Host-mk8oauth";
    private const int MaximumFormBytes = 16 * 1024;
    private static readonly string[] OAuthScopes =
        ["offline_access", "imap", "smtp", "pop", "jmap", "dav", "sieve"];
    private static readonly string[] OpenIdConnectScopes =
        ["openid", "profile", "email", .. OAuthScopes];

    private static string[] AdvertisedScopes(EnvironmentConfig environment) =>
        environment.OAuth.EnableOpenIdConnect ? OpenIdConnectScopes : OAuthScopes;

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/oauth-authorization-server", MetadataAsync);
        endpoints.MapGet("/.well-known/openid-configuration", OpenIdMetadataAsync);
        endpoints.MapGet("/oauth/jwks", JwksAsync);
        endpoints.MapMethods("/oauth/userinfo", [HttpMethods.Get, HttpMethods.Post], UserInfoAsync)
            .RequireRateLimiting("oauth-token");
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
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["issuer"] = baseUri.AbsoluteUri.TrimEnd('/'),
            ["authorization_endpoint"] = new Uri(baseUri, "oauth/authorize").AbsoluteUri,
            ["token_endpoint"] = new Uri(baseUri, "oauth/token").AbsoluteUri,
            ["revocation_endpoint"] = new Uri(baseUri, "oauth/revoke").AbsoluteUri,
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["scopes_supported"] = AdvertisedScopes(environment),
        };
        if (environment.OAuth.EnableOpenIdConnect)
        {
            metadata["jwks_uri"] = new Uri(baseUri, "oauth/jwks").AbsoluteUri;
            metadata["userinfo_endpoint"] = new Uri(baseUri, "oauth/userinfo").AbsoluteUri;
        }
        return Results.Json(metadata);
    }

    private static IResult OpenIdMetadataAsync(
        HttpContext context,
        EnvironmentConfig environment)
    {
        if (!environment.OAuth.EnableOpenIdConnect)
            return Results.NotFound();

        var baseUri = environment.OAuth.GetPublicBaseUri(
            environment.Smtp.Hostname,
            environment.Jmap.PublicBaseUrl);
        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["issuer"] = baseUri.AbsoluteUri.TrimEnd('/'),
            ["authorization_endpoint"] = new Uri(baseUri, "oauth/authorize").AbsoluteUri,
            ["token_endpoint"] = new Uri(baseUri, "oauth/token").AbsoluteUri,
            ["userinfo_endpoint"] = new Uri(baseUri, "oauth/userinfo").AbsoluteUri,
            ["jwks_uri"] = new Uri(baseUri, "oauth/jwks").AbsoluteUri,
            ["revocation_endpoint"] = new Uri(baseUri, "oauth/revoke").AbsoluteUri,
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["scopes_supported"] = OpenIdConnectScopes,
            ["claims_supported"] = new[]
            {
                "iss", "sub", "aud", "exp", "iat", "auth_time", "nonce", "at_hash",
                "preferred_username", "email", "email_verified",
            },
            ["claims_parameter_supported"] = false,
            ["request_parameter_supported"] = false,
            ["request_uri_parameter_supported"] = false,
        });
    }

    private static async Task<IResult> JwksAsync(
        HttpContext context,
        IGatewayOAuthClient application,
        EnvironmentConfig environment)
    {
        if (!environment.OAuth.EnableOpenIdConnect)
            return Results.NotFound();

        var key = await application.GetPublicKeyAsync(context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["keys"] = new[]
            {
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["kty"] = key.KeyType,
                    ["use"] = key.Use,
                    ["kid"] = key.KeyId,
                    ["alg"] = key.Algorithm,
                    ["n"] = key.Modulus,
                    ["e"] = key.Exponent,
                },
            },
        });
    }

    private static async Task<IResult> UserInfoAsync(
        HttpContext context,
        IGatewayOAuthClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        if (!environment.OAuth.EnableOpenIdConnect)
            return Results.NotFound();
        if (!TryGetBearerToken(context.Request, out var accessToken))
            return BearerError(context);

        var identity = (await application.AuthenticateIdentityAsync(
            new OAuthIdentityLookupRequest(accessToken, "openid"),
            cancellationToken).ConfigureAwait(false)).Identity;
        if (identity is null)
            return BearerError(context);

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = identity.UserId.ToString("D"),
        };
        if (identity.Scopes.Contains("email", StringComparer.Ordinal))
        {
            claims["email"] = identity.Username;
            claims["email_verified"] = true;
        }
        if (identity.Scopes.Contains("profile", StringComparer.Ordinal))
            claims["preferred_username"] = identity.Username;
        return Results.Json(claims);
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
            await WriteAuthorizationErrorAsync(context, environment, error, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request!.PromptNone)
        {
            await WriteAuthorizationErrorAsync(
                context,
                environment,
                new(
                    "login_required",
                    "Interactive sign-in is required.",
                    request.ClientId,
                    request.RedirectUri,
                    request.State),
                cancellationToken).ConfigureAwait(false);
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
        await WriteLoginPageAsync(context, request!, csrf, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteAuthorizationAsync(
        HttpContext context,
        IGatewayOAuthClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType
            || context.Request.ContentLength is > MaximumFormBytes)
        {
            await WritePlainErrorAsync(context, StatusCodes.Status400BadRequest,
                "The authorization form is not valid.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var parsed = TryParseAuthorizationRequest(
            name => form[name].ToString(),
            environment,
            out var request,
            out var error);
        if (!parsed)
        {
            await WriteAuthorizationErrorAsync(context, environment, error, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request!.PromptNone)
        {
            await WriteAuthorizationErrorAsync(
                context,
                environment,
                new(
                    "login_required",
                    "Interactive sign-in is required.",
                    request.ClientId,
                    request.RedirectUri,
                    request.State),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var csrf = form["csrf"].ToString();
        var cookie = context.Request.Cookies[CsrfCookieName] ?? string.Empty;
        if (!FixedTimeTextEquals(csrf, cookie) || csrf.Length < 32)
        {
            await WritePlainErrorAsync(context, StatusCodes.Status400BadRequest,
                "The authorization form expired. Start the sign-in again.", cancellationToken).ConfigureAwait(false);
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
                StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var authorization = await application.AuthorizeAsync(
            new OAuthAuthorizeApplicationRequest(
                username,
                password,
                mfaCode,
                request.ClientId,
                request.RedirectUri,
                deviceName,
                request.Scopes,
                request.CodeChallenge,
                request.Nonce),
            cancellationToken).ConfigureAwait(false);
        if (authorization.Outcome == OAuthAuthorizationOutcome.InvalidCredentials)
        {
            await WriteLoginPageAsync(
                context,
                request!,
                csrf,
                "The email address or password is not valid.",
                cancellationToken,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        if (authorization.Outcome == OAuthAuthorizationOutcome.InvalidVerificationCode)
        {
            await WriteLoginPageAsync(
                context,
                request!,
                csrf,
                "The email address, password, or verification code is not valid.",
                cancellationToken,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        if (authorization.Outcome != OAuthAuthorizationOutcome.Succeeded
            || authorization.AuthorizationCode is null)
        {
            await WritePlainErrorAsync(context, StatusCodes.Status400BadRequest,
                "The authorization could not be created.", cancellationToken).ConfigureAwait(false);
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
            [("code", authorization.AuthorizationCode), ("state", request.State)]));
    }

    private static async Task<IResult> ExchangeTokenAsync(
        HttpContext context,
        IGatewayOAuthClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        if (!context.Request.HasFormContentType
            || context.Request.ContentLength is > MaximumFormBytes)
        {
            return OAuthError("invalid_request", "Use a bounded form-encoded request.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var clientId = form["client_id"].ToString();
        if (!string.Equals(clientId, environment.OAuth.ClientId, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(form["client_secret"].ToString()))
        {
            return OAuthError("invalid_client", "The public client identifier is not valid.",
                StatusCodes.Status401Unauthorized);
        }

        OAuthTokenValue? pair;
        switch (form["grant_type"].ToString())
        {
            case "authorization_code":
                pair = (await application.RedeemAuthorizationCodeAsync(
                    new OAuthAuthorizationCodeRedeemRequest(
                        form["code"].ToString(),
                        clientId,
                        form["redirect_uri"].ToString(),
                        form["code_verifier"].ToString()),
                    cancellationToken).ConfigureAwait(false)).Token;
                break;
            case "refresh_token":
                pair = (await application.RefreshTokenAsync(
                    new OAuthRefreshTokenRequest(
                        form["refresh_token"].ToString(),
                        clientId),
                    cancellationToken).ConfigureAwait(false)).Token;
                break;
            default:
                return OAuthError("unsupported_grant_type", "The grant type is not supported.");
        }

        if (pair is null)
            return OAuthError("invalid_grant", "The authorization grant is invalid or expired.");

        var response = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["access_token"] = pair.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = pair.ExpiresInSeconds,
            ["refresh_token"] = pair.RefreshToken,
            ["scope"] = pair.Scope,
        };
        if (pair.IdToken is not null)
            response["id_token"] = pair.IdToken;
        return Results.Json(response);
    }

    private static async Task<IResult> RevokeTokenAsync(
        HttpContext context,
        IGatewayOAuthClient application,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        if (!context.Request.HasFormContentType
            || context.Request.ContentLength is > MaximumFormBytes)
        {
            return OAuthError("invalid_request", "Use a bounded form-encoded request.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var clientId = form["client_id"].ToString();
        if (!string.Equals(clientId, environment.OAuth.ClientId, StringComparison.Ordinal))
            return OAuthError("invalid_client", "The public client identifier is not valid.",
                StatusCodes.Status401Unauthorized);

        await application.RevokeTokenAsync(
            new OAuthRevokeTokenRequest(form["token"].ToString(), clientId),
            cancellationToken).ConfigureAwait(false);
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
        if (!string.Equals(getValue("response_type"), "code", StringComparison.Ordinal))
        {
            error = error with { Code = "unsupported_response_type", Description = "Only authorization code responses are supported." };
            return false;
        }
        if (state.Length is < 16 or > 1024 || state.Any(char.IsControl))
        {
            error = error with { Description = "A bounded state value is required." };
            return false;
        }
        if (!string.Equals(getValue("code_challenge_method"), "S256"
, StringComparison.Ordinal) || !OAuthProtocolValues.IsValidPkceChallenge(getValue("code_challenge")))
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
        var hasOpenId = scopes.Contains("openid", StringComparer.Ordinal);
        if (hasOpenId && !environment.OAuth.EnableOpenIdConnect
            || scopes.Any(scope => scope is "email" or "profile") && !hasOpenId)
        {
            error = error with { Code = "invalid_scope", Description = "The requested identity scopes are not valid." };
            return false;
        }

        var nonce = getValue("nonce");
        if (nonce.Length > 512 || nonce.Any(char.IsControl) || nonce.Length > 0 && !hasOpenId)
        {
            error = error with { Description = "The OpenID Connect nonce is not valid." };
            return false;
        }

        var prompt = getValue("prompt");
        var prompts = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prompts.Distinct(StringComparer.Ordinal).Count() != prompts.Length
            || prompts.Any(value => value is not ("none" or "login" or "consent" or "select_account"))
            || prompts.Contains("none", StringComparer.Ordinal) && prompts.Length != 1)
        {
            error = error with { Description = "The OpenID Connect prompt is not valid." };
            return false;
        }

        var maxAge = getValue("max_age");
        if (maxAge.Length > 0
            && (maxAge.Length > 10
                || maxAge.Any(character => character is < '0' or > '9')
                || !uint.TryParse(maxAge, out _)))
        {
            error = error with { Description = "The OpenID Connect maximum authentication age is not valid." };
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
            loginHint,
            nonce.Length == 0 ? null : nonce,
            prompt,
            maxAge,
            prompts.Contains("none", StringComparer.Ordinal));
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
            cancellationToken).ConfigureAwait(false);
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
            {Hidden("nonce", request.Nonce ?? string.Empty)}
            {Hidden("prompt", request.Prompt)}
            {Hidden("max_age", request.MaxAge)}
            {Hidden("csrf", csrf)}
            <p><label>Email address <input name="username" type="email" autocomplete="username" value="{encode(request.LoginHint)}" maxlength="320" required></label></p>
            <p><label>Password <input name="password" type="password" autocomplete="current-password" maxlength="1024" required></label></p>
            <p><label>Authenticator or recovery code (if enabled) <input name="mfa_code" autocomplete="one-time-code" maxlength="128"></label></p>
            <p><label>Device name <input name="device_name" value="Thunderbird" maxlength="128" required></label></p>
            <button type="submit">Authorize</button>
            </form>
            </main></body></html>
            """;
        await context.Response.WriteAsync(html, cancellationToken).ConfigureAwait(false);

        string Hidden(string name, string value) =>
            $"<input type=\"hidden\" name=\"{name}\" value=\"{encode(value)}\">";
    }

    private static IResult OAuthError(string code, string description, int statusCode = 400) =>
        Results.Json(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["error"] = code,
                ["error_description"] = description,
            },
            statusCode: statusCode);

    private static IResult BearerError(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return OAuthError(
            "invalid_token",
            "A valid OpenID Connect access token is required.",
            StatusCodes.Status401Unauthorized);
    }

    private static bool TryGetBearerToken(HttpRequest request, out string token)
    {
        const string prefix = "Bearer ";
        var value = request.Headers.Authorization.ToString();
        token = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : string.Empty;
        return token.Length is > 0 and <= 512
            && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static async Task WritePlainErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        SetSensitiveResponseHeaders(context.Response);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, cancellationToken).ConfigureAwait(false);
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
        string LoginHint,
        string? Nonce,
        string Prompt,
        string MaxAge,
        bool PromptNone);

    internal sealed record OAuthAuthorizationError(
        string Code,
        string Description,
        string ClientId,
        string? RedirectUri,
        string State);
}
