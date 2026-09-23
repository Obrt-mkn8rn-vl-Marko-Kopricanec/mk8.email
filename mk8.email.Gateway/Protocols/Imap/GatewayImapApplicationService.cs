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

    public Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
        ImapMailboxSubscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxSubscriptionRequest, ImapMailboxSubscriptionResult>(
            "imap", ApplicationOperations.ImapSetMailboxSubscription, request, cancellationToken);

    public Task<ImapMailboxCreateResult> CreateMailboxAsync(
        ImapMailboxCreateRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxCreateRequest, ImapMailboxCreateResult>(
            "imap", ApplicationOperations.ImapCreateMailbox, request, cancellationToken);

    public Task<ImapMailboxRenameResult> RenameMailboxAsync(
        ImapMailboxRenameRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxRenameRequest, ImapMailboxRenameResult>(
            "imap", ApplicationOperations.ImapRenameMailbox, request, cancellationToken);

    public Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
        ImapMailboxDeleteRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxDeleteRequest, ImapMailboxDeleteResult>(
            "imap", ApplicationOperations.ImapDeleteMailbox, request, cancellationToken);

    public Task<ImapMailboxSelectResult> SelectMailboxAsync(
        ImapMailboxSelectRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMailboxSelectRequest, ImapMailboxSelectResult>(
            "imap", ApplicationOperations.ImapSelectMailbox, request, cancellationToken);

    public Task<ImapQuotaResult> GetQuotaAsync(
        ImapQuotaRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapQuotaRequest, ImapQuotaResult>(
            "imap", ApplicationOperations.ImapGetQuota, request, cancellationToken);

    public Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
        ImapIdleSnapshotRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapIdleSnapshotRequest, ImapIdleSnapshotResult>(
            "imap", ApplicationOperations.ImapGetIdleSnapshot, request, cancellationToken);

    public Task<ImapExpungeResult> ExpungeDeletedAsync(
        ImapExpungeRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapExpungeRequest, ImapExpungeResult>(
            "imap", ApplicationOperations.ImapExpungeDeleted, request, cancellationToken);

    public Task<ImapStoreResult> StoreFlagsAsync(
        ImapStoreRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapStoreRequest, ImapStoreResult>(
            "imap", ApplicationOperations.ImapStoreFlags, request, cancellationToken);

    public Task<ImapMoveResult> MoveMessagesAsync(
        ImapMoveRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapMoveRequest, ImapMoveResult>(
            "imap", ApplicationOperations.ImapMoveMessages, request, cancellationToken);

    public Task<ImapCopyResult> CopyMessagesAsync(
        ImapCopyRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapCopyRequest, ImapCopyResult>(
            "imap", ApplicationOperations.ImapCopyMessages, request, cancellationToken);

    public Task<ImapAppendPreflightResult> CheckAppendCapacityAsync(
        ImapAppendPreflightRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapAppendPreflightRequest, ImapAppendPreflightResult>(
            "imap", ApplicationOperations.ImapCheckAppendCapacity, request, cancellationToken);

    public Task<ImapAppendResult> AppendMessagesAsync(
        ImapAppendRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapAppendRequest, ImapAppendResult>(
            "imap", ApplicationOperations.ImapAppendMessages, request, cancellationToken);

    public Task<ImapSearchResult> SearchMessagesAsync(
        ImapSearchRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<ImapSearchRequest, ImapSearchResult>(
            "imap", ApplicationOperations.ImapSearchMessages, request, cancellationToken);
}
