namespace mk8.email.Contracts.Messaging;

public enum OAuthAuthorizationOutcome
{
    Succeeded,
    InvalidCredentials,
    InvalidVerificationCode,
    Rejected,
}

public sealed record OAuthAuthorizeApplicationRequest(
    string Username,
    string Password,
    string VerificationCode,
    string ClientId,
    string RedirectUri,
    string DeviceName,
    IReadOnlyList<string> Scopes,
    string CodeChallenge,
    string? Nonce);

public sealed record OAuthAuthorizeApplicationResult(
    OAuthAuthorizationOutcome Outcome,
    string? AuthorizationCode = null);

public sealed record OAuthAuthorizationCodeRedeemRequest(
    string Code,
    string ClientId,
    string RedirectUri,
    string CodeVerifier);

public sealed record OAuthRefreshTokenRequest(
    string RefreshToken,
    string ClientId);

public sealed record OAuthTokenValue(
    Guid GrantId,
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    string Scope,
    string? IdToken);

public sealed record OAuthTokenApplicationResult(OAuthTokenValue? Token);

public sealed record OAuthIdentityLookupRequest(
    string AccessToken,
    string RequiredScope);

public sealed record OAuthIdentityValue(
    Guid UserId,
    string Username,
    IReadOnlyList<string> Scopes);

public sealed record OAuthIdentityLookupResult(OAuthIdentityValue? Identity);

public sealed record OAuthRevokeTokenRequest(
    string Token,
    string ClientId);

public sealed record OAuthTokenRevocationResult(bool Accepted);

public sealed record OAuthPublicKeyValue(
    string KeyType,
    string Use,
    string KeyId,
    string Algorithm,
    string Modulus,
    string Exponent);
