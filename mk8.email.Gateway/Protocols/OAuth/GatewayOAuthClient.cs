using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.OAuth;

public sealed class GatewayOAuthClient(IGatewayApplicationTransport transport)
    : IGatewayOAuthClient
{
    public Task<OAuthPublicKeyValue> GetPublicKeyAsync(
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<object, OAuthPublicKeyValue>(
            "oauth",
            ApplicationOperations.OAuthPublicKeyGet,
            new { },
            cancellationToken);

    public Task<OAuthIdentityLookupResult> AuthenticateIdentityAsync(
        OAuthIdentityLookupRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<OAuthIdentityLookupRequest, OAuthIdentityLookupResult>(
            "oauth",
            ApplicationOperations.OAuthIdentityAuthenticate,
            request,
            cancellationToken);

    public Task<OAuthAuthorizeApplicationResult> AuthorizeAsync(
        OAuthAuthorizeApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<OAuthAuthorizeApplicationRequest, OAuthAuthorizeApplicationResult>(
            "oauth",
            ApplicationOperations.OAuthAuthorize,
            request,
            cancellationToken);

    public Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(
        OAuthAuthorizationCodeRedeemRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<OAuthAuthorizationCodeRedeemRequest, OAuthTokenApplicationResult>(
            "oauth",
            ApplicationOperations.OAuthAuthorizationCodeRedeem,
            request,
            cancellationToken);

    public Task<OAuthTokenApplicationResult> RefreshTokenAsync(
        OAuthRefreshTokenRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<OAuthRefreshTokenRequest, OAuthTokenApplicationResult>(
            "oauth",
            ApplicationOperations.OAuthTokenRefresh,
            request,
            cancellationToken);

    public async Task RevokeTokenAsync(
        OAuthRevokeTokenRequest request,
        CancellationToken cancellationToken = default) =>
        _ = await transport.SendAsync<OAuthRevokeTokenRequest, OAuthTokenRevocationResult>(
            "oauth",
            ApplicationOperations.OAuthTokenRevoke,
            request,
            cancellationToken);
}
