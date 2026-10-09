using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace mk8.email.Jmap;

internal sealed class MailImportService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    JmapBlobService blobs,
    JmapEmailStore store,
    EnvironmentConfig environment) : IMailImportService
{
    public async Task<MailImportResult> ImportAsync(MailImportCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return Failure(MailImportStatus.AccountNotFound);
        if (command.Items.Count > environment.Jmap.MaxObjectsInSet)
            return Failure(MailImportStatus.RequestTooLarge);
        if (command.IfMailboxInState is not null && !string.Equals(command.IfMailboxInState,
            await states.GetStateAsync(account.InboxId, JmapConstants.MailboxDataType, cancellationToken).ConfigureAwait(false),
            StringComparison.Ordinal)) return Failure(MailImportStatus.StateMismatch);
        var oldState = await states.GetStateAsync(account.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null
            && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return Failure(MailImportStatus.StateMismatch);
        var outcomes = new List<MailImportItemOutcome>(command.Items.Count);
        foreach (var item in command.Items)
            outcomes.Add(await ImportItemAsync(item, account, cancellationToken).ConfigureAwait(false));
        var newState = await states.GetStateAsync(account.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailImportStatus.Ok, oldState, newState, outcomes);
    }

    private async Task<MailImportItemOutcome> ImportItemAsync(MailImportItem item,
        JmapAccount account, CancellationToken cancellationToken)
    {
        if (item.InvalidInitialProperties)
            return Failed(item, MailImportItemError.InvalidProperties);
        var blob = item.BlobId is null ? null
            : await blobs.GetAsync(account.InboxId, item.BlobId, cancellationToken).ConfigureAwait(false);
        if (blob is null)
            return Failed(item, MailImportItemError.MissingBlob);
        if (item.MailboxIssue != MailMessageMailboxIssue.None)
            return Failed(item, item.MailboxIssue == MailMessageMailboxIssue.TooMany
                ? MailImportItemError.TooManyMailboxes : MailImportItemError.InvalidMailbox);
        if (item.MailboxId is null)
            return Failed(item, MailImportItemError.InvalidMailbox);
        var folder = await database.Folders.FirstOrDefaultAsync(candidate =>
            candidate.Id == item.MailboxId.Value && candidate.InboxId == account.InboxId,
            cancellationToken).ConfigureAwait(false);
        if (folder is null)
            return Failed(item, MailImportItemError.InvalidMailbox);
        if (item.KeywordIssue != MailMessageKeywordIssue.None)
            return Failed(item, item.KeywordIssue == MailMessageKeywordIssue.TooMany
                ? MailImportItemError.TooManyKeywords : MailImportItemError.InvalidKeywords);
        if (item.InvalidReceivedAt)
            return Failed(item, MailImportItemError.InvalidReceivedAt);
        var receivedAt = item.ReceivedAt ?? MailArrivalDate.DetermineReceivedAt(blob.Content);
        var stored = await store.StoreAsync(account, folder, blob.Content,
            item.Keywords.ToHashSet(StringComparer.Ordinal), receivedAt, cancellationToken).ConfigureAwait(false);
        if (stored.Error is not null)
        {
            return Failed(item, stored.Error.Error switch
            {
                MailMessageMutationError.TooLarge => MailImportItemError.TooLarge,
                MailMessageMutationError.OverQuota => MailImportItemError.OverQuota,
                MailMessageMutationError.InvalidEmail => MailImportItemError.InvalidEmail,
                _ => throw new InvalidOperationException("The message store returned an invalid import failure."),
            });
        }
        var email = stored.Email
            ?? throw new InvalidOperationException("The message store returned no imported message.");
        return new(item.CreationId, MailImportItemError.None, email.Id,
            email.ThreadObjectId ?? email.Id.ToString("N"), email.SizeBytes);
    }

    private static MailImportItemOutcome Failed(MailImportItem item, MailImportItemError error) =>
        new(item.CreationId, error, null, null, null);

    private static MailImportResult Failure(MailImportStatus status) => new(status, null, null, []);
}
