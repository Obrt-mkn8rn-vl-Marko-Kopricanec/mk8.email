using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Interfaces;

public interface IOAuthApplicationService
{
    Task<OAuthPublicKeyValue> GetPublicKeyAsync(
        CancellationToken cancellationToken = default);

    Task<OAuthIdentityLookupResult> AuthenticateIdentityAsync(
        OAuthIdentityLookupRequest request,
        CancellationToken cancellationToken = default);

    Task<OAuthAuthorizeApplicationResult> AuthorizeAsync(
        OAuthAuthorizeApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(
        OAuthAuthorizationCodeRedeemRequest request,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenApplicationResult> RefreshTokenAsync(
        OAuthRefreshTokenRequest request,
        CancellationToken cancellationToken = default);

    Task RevokeTokenAsync(
        OAuthRevokeTokenRequest request,
        CancellationToken cancellationToken = default);
}
