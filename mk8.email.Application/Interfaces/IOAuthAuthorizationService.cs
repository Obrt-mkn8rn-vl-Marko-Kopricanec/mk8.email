namespace mk8.email.Application.Interfaces;

public interface IOAuthAuthorizationService
{
    // OAuth redirect URIs are compared as exact client-supplied strings; Uri parsing
    // would normalize the security-sensitive value and change this published contract.
#pragma warning disable CA1054
    Task<string?> CreateAuthorizationCodeAsync(
        Guid userId,
        string clientId,
        string redirectUri,
        string deviceName,
        IReadOnlyCollection<string> scopes,
        string codeChallenge,
        string? nonce = null,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenPair?> RedeemAuthorizationCodeAsync(
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default);
#pragma warning restore CA1054
}
