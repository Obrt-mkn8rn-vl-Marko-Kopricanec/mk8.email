using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Imap;

public sealed class GatewayImapApplicationService(
    IGatewayApplicationTransport transport) : IImapApplicationService
{
    public Task<ImapIdentityResult> AuthenticatePasswordAsync(
        ImapPasswordAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapPasswordAuthentication, ImapIdentityResult>(
            "imap", ApplicationOperations.ImapAuthenticatePassword, request, cancellationToken);

    public Task<ImapIdentityResult> AuthenticateOAuthAsync(
        ImapOAuthAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapOAuthAuthentication, ImapIdentityResult>(
            "imap", ApplicationOperations.ImapAuthenticateOAuth, request, cancellationToken);
}
