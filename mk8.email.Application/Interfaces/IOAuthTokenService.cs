namespace mk8.email.Application.Interfaces;

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

    async Task<OAuthAccessTokenIdentity?> AuthenticateIdentityAsync(
        string accessToken,
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAccessTokenAsync(
            accessToken,
            requiredScope,
            cancellationToken).ConfigureAwait(false);
        return user is null
            ? null
            : new(user.Id, user.Username, [requiredScope]);
    }

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
