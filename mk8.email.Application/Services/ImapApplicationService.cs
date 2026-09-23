using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Imap;

namespace mk8.email.Application.Services;

internal sealed class ImapApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens) : IImapApplicationService
{
    public async Task<ImapIdentityResult> AuthenticatePasswordAsync(
        ImapPasswordAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await authenticator.AuthenticateAsync(
            request.Username, request.Password, cancellationToken);
        return new ImapIdentityResult(user?.Id, user?.Username);
    }

    public async Task<ImapIdentityResult> AuthenticateOAuthAsync(
        ImapOAuthAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await oauthTokens.AuthenticateAccessTokenAsync(
            request.AccessToken, "imap", cancellationToken);
        return user is null
            || !string.Equals(request.Username, user.Username, StringComparison.OrdinalIgnoreCase)
            ? new ImapIdentityResult(null, null)
            : new ImapIdentityResult(user.Id, user.Username);
    }
}
