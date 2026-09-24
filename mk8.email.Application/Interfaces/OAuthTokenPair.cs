namespace mk8.email.Application.Interfaces;

public sealed record OAuthTokenPair(
    Guid GrantId,
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    string Scope,
    string? IdToken = null);
