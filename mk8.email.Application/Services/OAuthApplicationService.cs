using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Services;

public sealed class OAuthApplicationService(
    IMailAuthenticator authenticator,
    IMfaService mfa,
    IOAuthAuthorizationService authorization,
    IOAuthTokenService tokens,
    IOpenIdConnectService openIdConnect) : IOAuthApplicationService
{
    public Task<OAuthPublicKeyValue> GetPublicKeyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = openIdConnect.GetPublicKey();
        return Task.FromResult(new OAuthPublicKeyValue(
            key.KeyType,
            key.Use,
            key.KeyId,
            key.Algorithm,
            key.Modulus,
            key.Exponent));
    }

    public async Task<OAuthIdentityLookupResult> AuthenticateIdentityAsync(
        OAuthIdentityLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        var identity = await tokens.AuthenticateIdentityAsync(
            request.AccessToken,
            request.RequiredScope,
            cancellationToken).ConfigureAwait(false);
        return new OAuthIdentityLookupResult(identity is null
            ? null
            : new OAuthIdentityValue(identity.UserId, identity.Username, identity.Scopes));
    }

    public async Task<OAuthAuthorizeApplicationResult> AuthorizeAsync(
        OAuthAuthorizeApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await authenticator.AuthenticatePrimaryAsync(
            request.Username,
            request.Password,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new OAuthAuthorizeApplicationResult(
                OAuthAuthorizationOutcome.InvalidCredentials);
        }

        var verification = await mfa.VerifyForAuthenticationAsync(
            user.Id,
            request.VerificationCode,
            cancellationToken).ConfigureAwait(false);
        if (verification == MfaVerificationResult.Failed)
        {
            return new OAuthAuthorizeApplicationResult(
                OAuthAuthorizationOutcome.InvalidVerificationCode);
        }

        var code = await authorization.CreateAuthorizationCodeAsync(
            user.Id,
            request.ClientId,
            request.RedirectUri,
            request.DeviceName,
            request.Scopes,
            request.CodeChallenge,
            request.Nonce,
            cancellationToken).ConfigureAwait(false);
        return code is null
            ? new OAuthAuthorizeApplicationResult(OAuthAuthorizationOutcome.Rejected)
            : new OAuthAuthorizeApplicationResult(
                OAuthAuthorizationOutcome.Succeeded,
                code);
    }

    public async Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(
        OAuthAuthorizationCodeRedeemRequest request,
        CancellationToken cancellationToken = default) =>
        Map(await authorization.RedeemAuthorizationCodeAsync(
            request.Code,
            request.ClientId,
            request.RedirectUri,
            request.CodeVerifier,
            cancellationToken).ConfigureAwait(false));

    public async Task<OAuthTokenApplicationResult> RefreshTokenAsync(
        OAuthRefreshTokenRequest request,
        CancellationToken cancellationToken = default) =>
        Map(await tokens.RefreshAsync(
            request.RefreshToken,
            request.ClientId,
            cancellationToken).ConfigureAwait(false));

    public Task RevokeTokenAsync(
        OAuthRevokeTokenRequest request,
        CancellationToken cancellationToken = default) =>
        tokens.RevokeTokenAsync(request.Token, request.ClientId, cancellationToken);

    private static OAuthTokenApplicationResult Map(OAuthTokenPair? pair) =>
        new(pair is null
            ? null
            : new OAuthTokenValue(
                pair.GrantId,
                pair.AccessToken,
                pair.RefreshToken,
                pair.ExpiresInSeconds,
                pair.Scope,
                pair.IdToken));
}
