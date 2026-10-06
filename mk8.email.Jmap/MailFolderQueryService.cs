using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailFolderQueryService(
    JmapAccountService accounts,
    JmapMailboxStore folders,
    JmapStateService states,
    EmailDbContext database) : IMailFolderQueryService
{
    public async Task<MailFolderQueryResult> QueryAsync(
        MailFolderQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailFolderQueryStatus.AccountNotFound, null, 0, [], 0);
        if (command.CheckAccountOnly)
            return new(MailFolderQueryStatus.Authorized, null, 0, [], 0);
        await MailFolderAccountLock.AcquireAsync(database, account.InboxId, cancellationToken).ConfigureAwait(false);
        account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return new(MailFolderQueryStatus.AccountNotFound, null, 0, [], 0);
        var ordered = await LoadSortedAsync(account.InboxId, command.Criteria, cancellationToken)
            .ConfigureAwait(false);
        var ids = ordered.Select(folder => folder.Id).ToArray();
        var position = command.Position;
        if (command.AnchorId is { } anchor)
        {
            if (!command.AnchorCanMatch)
                return new(MailFolderQueryStatus.AnchorNotFound, null, 0, [], 0);
            var index = Array.IndexOf(ids, anchor);
            if (index < 0)
                return new(MailFolderQueryStatus.AnchorNotFound, null, 0, [], 0);
            position = Math.Min(9_007_199_254_740_991L, Math.Max(0L, index + command.AnchorOffset));
        }
        else if (position < 0)
            position = Math.Max(0L, ids.Length + position);
        var pagePosition = position >= ids.Length ? ids.Length : checked((int)position);
        var page = pagePosition >= ids.Length ? [] : ids.Skip(pagePosition).Take(command.Limit).ToArray();
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.MailboxDataType, cancellationToken)
            .ConfigureAwait(false);
        return new(MailFolderQueryStatus.Ok, state, position, page, ids.Length);
    }

    public async Task<MailFolderQueryChangesResult> QueryChangesAsync(
        MailFolderQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailFolderQueryStatus.AccountNotFound, null, [], [], 0);
        if (command.CheckAccountOnly)
            return new(MailFolderQueryStatus.Authorized, null, [], [], 0);
        await MailFolderAccountLock.AcquireAsync(database, account.InboxId, cancellationToken).ConfigureAwait(false);
        account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return new(MailFolderQueryStatus.AccountNotFound, null, [], [], 0);
        var ordered = await LoadSortedAsync(account.InboxId, command.Criteria, cancellationToken)
            .ConfigureAwait(false);
        var changes = await states.GetChangesAsync(account.InboxId, JmapConstants.MailboxDataType,
            command.SinceState, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (changes is null)
            return new(MailFolderQueryStatus.CannotCalculateChanges, null, [], [], 0);
        var currentIds = ordered.Select(folder => folder.Id).ToArray();
        var currentSet = currentIds.Select(FolderId).ToHashSet(StringComparer.Ordinal);
        var removed = changes.Destroyed.Concat(changes.Updated).Distinct(StringComparer.Ordinal).ToArray();
        var changedCurrent = changes.Created.Concat(changes.Updated)
            .Where(currentSet.Contains).ToHashSet(StringComparer.Ordinal);
        var added = currentIds.Select((id, index) => new MailFolderIndexedId(id, index))
            .Where(item => changedCurrent.Contains(FolderId(item.Id))).ToArray();
        if (command.MaxChanges is not null && removed.LongLength + added.LongLength > command.MaxChanges.Value)
            return new(MailFolderQueryStatus.TooManyChanges, null, [], [], 0);
        return new(MailFolderQueryStatus.Ok, changes.NewState, removed, added, currentIds.Length);
    }

    private async Task<IReadOnlyList<JmapMailboxView>> LoadSortedAsync(
        Guid accountId,
        MailFolderQueryCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (criteria is null || criteria.Sort is null)
            throw new InvalidOperationException("The folder query criteria are incomplete.");
        var all = await folders.LoadAsync(accountId, cancellationToken).ConfigureAwait(false);
        var filtered = MailFolderQueryEngine.Filter(all, criteria.Filter, criteria.FilterAsTree);
        return MailFolderQueryEngine.Sort(all, filtered, criteria.Sort, criteria.SortAsTree);
    }

    private static string FolderId(Guid id) => $"M{id:N}";
}
