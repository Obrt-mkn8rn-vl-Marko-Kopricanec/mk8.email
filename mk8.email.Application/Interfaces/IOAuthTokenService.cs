namespace mk8.email.Application.Interfaces;

public sealed record OAuthTokenPair(
    Guid GrantId,
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    string Scope);

public sealed record OAuthGrantSummary(
    Guid Id,
    string ClientId,
    string DeviceName,
    IReadOnlyList<string> Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);

public interface IOAuthTokenService
{
    Task<OAuthTokenPair?> CreateGrantAsync(
        Guid userId,
        string clientId,
        string deviceName,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenPair?> RefreshAsync(
        string refreshToken,
        string clientId,
        CancellationToken cancellationToken = default);

    Task<AuthenticatedMailUser?> AuthenticateAccessTokenAsync(
        string accessToken,
        string requiredScope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OAuthGrantSummary>> ListGrantsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeGrantAsync(
        Guid userId,
        Guid grantId,
        CancellationToken cancellationToken = default);

    Task RevokeTokenAsync(
        string token,
        string clientId,
        CancellationToken cancellationToken = default);
}
