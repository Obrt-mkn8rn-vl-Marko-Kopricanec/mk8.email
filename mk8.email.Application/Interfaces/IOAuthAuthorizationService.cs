namespace mk8.email.Application.Interfaces;

public interface IOAuthAuthorizationService
{
    Task<string?> CreateAuthorizationCodeAsync(
        Guid userId,
        string clientId,
        string redirectUri,
        string deviceName,
        IReadOnlyCollection<string> scopes,
        string codeChallenge,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenPair?> RedeemAuthorizationCodeAsync(
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default);
}
