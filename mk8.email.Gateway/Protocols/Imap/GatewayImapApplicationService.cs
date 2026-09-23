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

    public Task<ImapMailboxListResult> ListMailboxesAsync(
        ImapMailboxListRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxListRequest, ImapMailboxListResult>(
            "imap", ApplicationOperations.ImapListMailboxes, request, cancellationToken);

    public Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
        ImapMailboxStatusRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxStatusRequest, ImapMailboxStatusResult>(
            "imap", ApplicationOperations.ImapGetMailboxStatuses, request, cancellationToken);
}
