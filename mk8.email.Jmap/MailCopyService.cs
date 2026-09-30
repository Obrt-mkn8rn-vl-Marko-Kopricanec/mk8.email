using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace mk8.email.Jmap;

internal sealed class MailCopyService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    JmapEmailStore store,
    MailboxMessageContentService content,
    EnvironmentConfig environment) : IMailCopyService
{
    public async Task<MailCopyResult> CopyAsync(MailCopyCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        if (command.Items.Count > environment.Jmap.MaxObjectsInSet)
            return Failure(MailCopyStatus.RequestTooLarge);
        var sourceAccount = await accounts.GetAccountByInboxIdAsync(user, command.SourceAccountId,
            cancellationToken).ConfigureAwait(false);
        if (sourceAccount is null) return Failure(MailCopyStatus.SourceAccountNotFound);
        var targetAccount = await accounts.GetAccountByInboxIdAsync(user, command.TargetAccountId,
            cancellationToken).ConfigureAwait(false);
        if (targetAccount is null) return Failure(MailCopyStatus.TargetAccountNotFound);
        var sourceState = await states.GetStateAsync(sourceAccount.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        var oldTargetState = await states.GetStateAsync(targetAccount.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfFromInState is not null
                && !string.Equals(command.IfFromInState, sourceState, StringComparison.Ordinal)
            || command.IfInState is not null
                && !string.Equals(command.IfInState, oldTargetState, StringComparison.Ordinal))
            return Failure(MailCopyStatus.StateMismatch);

        var outcomes = new List<MailCopyItemOutcome>(command.Items.Count);
        var copiedSourceIds = new List<Guid>();
        foreach (var item in command.Items)
        {
            var outcome = await CopyItemAsync(item, sourceAccount, targetAccount,
                cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            if (outcome.Error == MailCopyItemError.None)
                copiedSourceIds.Add(item.SourceEmailId!.Value);
        }
        var newTargetState = await states.GetStateAsync(targetAccount.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        MailCopyDestroyOutcome? destroy = null;
        if (command.DestroyOriginal && copiedSourceIds.Count > 0)
            destroy = await DestroyCopiedAsync(command, sourceAccount, copiedSourceIds,
                cancellationToken).ConfigureAwait(false);
        return new(MailCopyStatus.Ok, oldTargetState, newTargetState, outcomes, destroy);
    }

    private async Task<MailCopyItemOutcome> CopyItemAsync(MailCopyItem item,
        JmapAccount sourceAccount, JmapAccount targetAccount, CancellationToken cancellationToken)
    {
        if (item.InvalidInitialProperties || item.SourceEmailId is null)
            return Failed(item, MailCopyItemError.InvalidProperties);
        var source = await database.Emails.AsNoTracking().FirstOrDefaultAsync(email =>
            email.Id == item.SourceEmailId.Value
            && email.Folder.InboxId == sourceAccount.InboxId && !email.IsDeleted,
            cancellationToken).ConfigureAwait(false);
        if (source is null) return Failed(item, MailCopyItemError.NotFound);
        if (item.MailboxIssue != MailMessageMailboxIssue.None)
            return Failed(item, item.MailboxIssue == MailMessageMailboxIssue.TooMany
                ? MailCopyItemError.TooManyMailboxes : MailCopyItemError.InvalidMailbox);
        if (item.MailboxId is null)
            return Failed(item, MailCopyItemError.InvalidMailbox);
        var folder = await database.Folders.FirstOrDefaultAsync(candidate =>
            candidate.Id == item.MailboxId.Value && candidate.InboxId == targetAccount.InboxId,
            cancellationToken).ConfigureAwait(false);
        if (folder is null) return Failed(item, MailCopyItemError.InvalidMailbox);
        if (item.KeywordIssue != MailMessageKeywordIssue.None)
            return Failed(item, item.KeywordIssue == MailMessageKeywordIssue.TooMany
                ? MailCopyItemError.TooManyKeywords : MailCopyItemError.InvalidKeywords);
        if (item.InvalidReceivedAt)
            return Failed(item, MailCopyItemError.InvalidReceivedAt);
        var keywords = item.Keywords is null
            ? MailMessageFlagMutations.Keywords(source)
            : item.Keywords.ToHashSet(StringComparer.Ordinal);
        var raw = await content.ReadAsync(source, cancellationToken).ConfigureAwait(false);
        var stored = await store.StoreAsync(targetAccount, folder, raw, keywords,
            item.ReceivedAt ?? source.ReceivedAt, cancellationToken).ConfigureAwait(false);
        if (stored.Error is not null)
        {
            var type = stored.Error["type"]?.GetValue<string>();
            return Failed(item, type switch
            {
                "tooLarge" => MailCopyItemError.TooLarge,
                "overQuota" => MailCopyItemError.OverQuota,
                "invalidEmail" => MailCopyItemError.InvalidEmail,
                _ => throw new InvalidOperationException("The message store returned an invalid copy failure."),
            });
        }
        var copied = stored.Email
            ?? throw new InvalidOperationException("The message store returned no copied message.");
        return new(item.CreationId, MailCopyItemError.None, copied.Id,
            copied.ThreadObjectId ?? copied.Id.ToString("N"), copied.SizeBytes);
    }

    private async Task<MailCopyDestroyOutcome> DestroyCopiedAsync(MailCopyCommand command,
        JmapAccount sourceAccount, IReadOnlyList<Guid> copiedSourceIds,
        CancellationToken cancellationToken)
    {
        var oldState = await states.GetStateAsync(sourceAccount.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.DestroyFromIfInState is not null
            && !string.Equals(command.DestroyFromIfInState, oldState, StringComparison.Ordinal))
            return new(MailCopyDestroyStatus.StateMismatch, null, null, [], []);
        var destroyed = new List<Guid>();
        var notFound = new List<Guid>();
        foreach (var id in copiedSourceIds.Distinct())
        {
            var source = await database.Emails.Include(email => email.Folder)
                .FirstOrDefaultAsync(email => email.Id == id
                    && email.Folder.InboxId == sourceAccount.InboxId && !email.IsDeleted,
                    cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                notFound.Add(id);
                continue;
            }
            await database.ExpungedUids.AddAsync(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = source.Uid,
                ModSeq = ++source.Folder.HighestModSeq,
                FolderId = source.FolderId,
            }, cancellationToken).ConfigureAwait(false);
            content.DeleteOnCommit(source);
            database.Emails.Remove(source);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            destroyed.Add(id);
        }
        var newState = await states.GetStateAsync(sourceAccount.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailCopyDestroyStatus.Completed, oldState, newState, destroyed, notFound);
    }

    private static MailCopyItemOutcome Failed(MailCopyItem item, MailCopyItemError error) =>
        new(item.CreationId, error, null, null, null);

    private static MailCopyResult Failure(MailCopyStatus status) => new(status, null, null, [], null);
}
