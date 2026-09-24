namespace mk8.email.Application.Interfaces;

public interface IOpenIdConnectService
{
    OpenIdConnectPublicKey GetPublicKey();

    string CreateIdToken(
        Guid userId,
        string username,
        string clientId,
        string accessToken,
        DateTime authenticatedAt,
        DateTime issuedAt,
        string? nonce,
        IReadOnlyCollection<string> scopes);
}
