using mk8.email.Contracts.Messaging;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailMimeDraftBuilder builder,
    JmapEmailStore store,
    MailboxMessageContentService content) : IMailMessageMutationService
{
    public async Task<MailMessageMutationResult> MutateAsync(
        MailMessageMutationCommand command,
        AuthenticatedMailUser user,
        JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailMessageMutationStatus.AccountNotFound, null, null, [], [], []);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null
            && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return new(MailMessageMutationStatus.StateMismatch, null, null, [], [], []);

        var created = new List<MailMessageCreateOutcome>(command.Creates.Count);
        var updated = new List<MailMessageUpdateOutcome>(command.Updates.Count);
        var destroyed = new List<MailMessageDestroyOutcome>(command.Destroys.Count);
        foreach (var item in command.Creates)
            created.Add(await CreateAsync(account, item, context, cancellationToken).ConfigureAwait(false));
        foreach (var item in command.Updates)
            updated.Add(await UpdateOneAsync(account.InboxId, item, context, cancellationToken)
                .ConfigureAwait(false));
        foreach (var item in command.Destroys)
            destroyed.Add(await DestroyOneAsync(account.InboxId, item, context, cancellationToken)
                .ConfigureAwait(false));
        var newState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailMessageMutationStatus.Ok, oldState, newState, created, updated, destroyed);
    }

    private async Task<MailMessageCreateOutcome> CreateAsync(
        JmapAccount account, MailMessageCreate item, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var value = item.Draft;
        if (!JmapId.IsValidId(item.CreationId))
            return new(item.CreationId, null, Failure(MailMessageMutationError.InvalidProperties));
        var mailbox = await ResolveDraftMailboxAsync(account.InboxId, value, context,
            cancellationToken).ConfigureAwait(false);
        if (mailbox.Failure is not null)
            return new(item.CreationId, null, mailbox.Failure);
        if (value.KeywordIssue != MailMessageKeywordIssue.None)
            return new(item.CreationId, null, Failure(value.KeywordIssue == MailMessageKeywordIssue.TooMany
                ? MailMessageMutationError.TooManyKeywords : MailMessageMutationError.InvalidProperties));
        if (value.InvalidReceivedAt)
            return new(item.CreationId, null, Failure(MailMessageMutationError.InvalidProperties, properties: ["receivedAt"]));
        var built = await builder.BuildAsync(account.InboxId, account.Address, context, value,
            cancellationToken).ConfigureAwait(false);
        if (built.Failure is not null)
            return new(item.CreationId, null, built.Failure);
        var stored = await store.StoreAsync(account, mailbox.Folder!, built.Raw!,
            value.Keywords.ToHashSet(StringComparer.Ordinal), value.ReceivedAt ?? DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (stored.Error is not null)
            return new(item.CreationId, null, stored.Error);
        var email = stored.Email!;
        context.CreatedIds[item.CreationId] = JmapId.Email(email.Id);
        return new(item.CreationId, new(email.Id,
            email.ThreadObjectId ?? email.Id.ToString("N"), email.SizeBytes), null);
    }

    private async Task<MailMessageUpdateOutcome> UpdateOneAsync(
        Guid accountId, MailMessageUpdate item, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var resolvedId = context.ResolveId(item.RequestedId);
        if (!JmapId.TryParseEmail(resolvedId, out var emailId))
            return new(item.RequestedId, null, Failure(MailMessageMutationError.NotFound));
        var error = await UpdateAsync(accountId, emailId, context, item.Patch, cancellationToken)
            .ConfigureAwait(false);
        return error is null
            ? new(item.RequestedId, emailId, null)
            : new(item.RequestedId, null, error);
    }

    private async Task<MailMessageDestroyOutcome> DestroyOneAsync(
        Guid accountId, MailMessageDestroy item, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var resolvedId = context.ResolveId(item.RequestedId);
        if (!JmapId.TryParseEmail(resolvedId, out var emailId))
            return new(item.RequestedId, null, Failure(MailMessageMutationError.NotFound));
        var error = await DestroyAsync(accountId, emailId, cancellationToken).ConfigureAwait(false);
        return error is null
            ? new(item.RequestedId, emailId, null)
            : new(item.RequestedId, null, error);
    }

    private static MailMessageMutationFailure Failure(
        MailMessageMutationError type, string? description = null, IReadOnlyList<string>? properties = null) =>
        new(type, description, properties, null);



    private async Task<MailMessageMutationFailure?> UpdateAsync(Guid accountId, Guid emailId,
        JmapInvocationContext context, MailMessagePatch patch, CancellationToken cancellationToken)
    {
        var email = await database.Emails.Include(item => item.Folder).FirstOrDefaultAsync(item => item.Id == emailId
            && item.Folder.InboxId == accountId && !item.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (email is null) return Failure(MailMessageMutationError.NotFound);
        var assertionFailure = await VerifyAsync(email, patch, cancellationToken).ConfigureAwait(false);
        if (assertionFailure is not null) return assertionFailure;
        var folders = MailMessageFlagMutations.Apply(patch, MailMessageFlagField.Folders, [JmapId.Mailbox(email.FolderId)]);
        var mailbox = await ResolveMailboxAsync(accountId, folders, context, cancellationToken).ConfigureAwait(false);
        if (mailbox.Failure is not null) return mailbox.Failure;
        var currentKeywords = MailMessageFlagMutations.Keywords(email);
        var entries = MailMessageFlagMutations.Apply(patch, MailMessageFlagField.Keywords, currentKeywords);
        var (keywords, keywordFailure) = MailMessageFlagMutations.ReadKeywords(entries);
        if (keywordFailure is not null) return keywordFailure;
        if (mailbox.Folder!.Id == email.FolderId && currentKeywords.SetEquals(keywords)) return null;
        await ApplyUpdateAsync(email, mailbox.Folder, keywords, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<MailMessageMutationFailure?> VerifyAsync(EmailDB email, MailMessagePatch patch,
        CancellationToken cancellationToken)
    {
        if (!patch.RequiresMime) return patch.Failure;
        try
        {
            var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
            using var message = JmapEmailCodec.Parse(raw);
            var snapshot = JmapEmailCodec.Capture(message, email.Id, raw.Length, true, email);
            return patch.Failure ?? MailMessageAssertions.Verify(snapshot, patch);
        }
        catch (FormatException)
        {
            return Failure(MailMessageMutationError.InvalidProperties, "The immutable MIME representation could not be verified.");
        }
    }

    private async Task ApplyUpdateAsync(EmailDB email, FolderDB target, HashSet<string> keywords,
        CancellationToken cancellationToken)
    {
        if (target.Id != email.FolderId)
        {
            var source = email.Folder;
            await database.ExpungedUids.AddAsync(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = email.Uid,
                ModSeq = ++source.HighestModSeq,
                FolderId = source.Id,
            }, cancellationToken).ConfigureAwait(false);
            email.FolderId = target.Id;
            email.Folder = target;
            email.Uid = target.NextUid++;
            email.ModSeq = ++target.HighestModSeq;
        }
        else email.ModSeq = ++email.Folder.HighestModSeq;
        JmapEmailStore.ApplyKeywords(email, keywords);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MailMessageMutationFailure?> DestroyAsync(
        Guid accountId,
        Guid emailId,
        CancellationToken cancellationToken)
    {
        var email = await database.Emails
            .Include(item => item.Folder)
            .FirstOrDefaultAsync(item => item.Id == emailId
                && item.Folder.InboxId == accountId
                && !item.IsDeleted,
                cancellationToken).ConfigureAwait(false);
        if (email is null)
            return Failure(MailMessageMutationError.NotFound);
        await database.ExpungedUids.AddAsync(new ExpungedUidDB
        {
            Id = Guid.CreateVersion7(),
            Uid = email.Uid,
            ModSeq = ++email.Folder.HighestModSeq,
            FolderId = email.FolderId,
        }, cancellationToken).ConfigureAwait(false);
        content.DeleteOnCommit(email);
        database.Emails.Remove(email);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<MailboxResult> ResolveMailboxAsync(Guid accountId, IReadOnlyList<MailMessageFlagEntry>? entries,
        JmapInvocationContext context, CancellationToken cancellationToken)
    {
        if (entries is null || entries.Count == 0) return new(null, Failure(MailMessageMutationError.InvalidProperties));
        if (entries.Count > 1) return new(null, Failure(MailMessageMutationError.TooManyMailboxes));
        var entry = entries[0];
        if (entry.Value != MailMessageFlagValue.Enabled
            || !JmapId.TryParseMailbox(context.ResolveId(entry.Key), out var folderId))
            return new(null, Failure(MailMessageMutationError.InvalidProperties));
        var folder = await database.Folders.FirstOrDefaultAsync(candidate => candidate.Id == folderId
            && candidate.InboxId == accountId, cancellationToken).ConfigureAwait(false);
        return folder is null ? new(null, Failure(MailMessageMutationError.InvalidProperties)) : new(folder, null);
    }

    private async Task<MailboxResult> ResolveDraftMailboxAsync(Guid accountId, MailMessageDraft draft,
        JmapInvocationContext context, CancellationToken cancellationToken)
    {
        if (draft.FolderIssue != MailMessageMailboxIssue.None)
            return new(null, Failure(draft.FolderIssue == MailMessageMailboxIssue.TooMany
                ? MailMessageMutationError.TooManyMailboxes : MailMessageMutationError.InvalidProperties));
        if (!JmapId.TryParseMailbox(context.ResolveId(draft.FolderReference!), out var folderId))
            return new(null, Failure(MailMessageMutationError.InvalidProperties));
        var folder = await database.Folders.FirstOrDefaultAsync(candidate => candidate.Id == folderId
            && candidate.InboxId == accountId, cancellationToken).ConfigureAwait(false);
        return folder is null ? new(null, Failure(MailMessageMutationError.InvalidProperties)) : new(folder, null);
    }

    private sealed record MailboxResult(FolderDB? Folder, MailMessageMutationFailure? Failure);
}
