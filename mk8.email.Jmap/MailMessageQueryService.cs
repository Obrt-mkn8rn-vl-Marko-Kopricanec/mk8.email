using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailMessageQueryService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailboxMessageContentService content) : IMailMessageQueryService
{
    public async Task<MailMessageQueryResult> QueryAsync(
        MailMessageQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailMessageQueryStatus.AccountNotFound, null, 0, [], 0);
        if (command.CheckAccountOnly)
            return new(MailMessageQueryStatus.Authorized, null, 0, [], 0);
        var all = await JmapEmailQueryEngine.LoadAsync(database, content, account.InboxId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var ordered = FilterAndSort(all, command.Criteria);
            var ids = ordered.Select(item => item.Email.Id).ToArray();
            var position = command.Position;
            if (command.AnchorId is { } anchor)
            {
                var anchorIndex = Array.FindIndex(ids,
                    id => string.Equals(EmailId(id), anchor, StringComparison.Ordinal));
                if (anchorIndex < 0)
                    return new(MailMessageQueryStatus.AnchorNotFound, null, 0, [], 0);
                position = Math.Min(9_007_199_254_740_991L,
                    Math.Max(0L, anchorIndex + command.AnchorOffset));
            }
            else if (position < 0)
                position = Math.Max(0L, ids.Length + position);
            var pagePosition = position >= ids.Length ? ids.Length : checked((int)position);
            var page = pagePosition >= ids.Length ? [] : ids.Skip(pagePosition).Take(command.Limit).ToArray();
            var state = await states.GetStateAsync(account.InboxId, JmapConstants.EmailDataType,
                cancellationToken).ConfigureAwait(false);
            return new(MailMessageQueryStatus.Ok, state, position, page, ids.Length);
        }
        finally
        {
            foreach (var item in all) item.Dispose();
        }
    }

    public async Task<MailMessageQueryChangesResult> QueryChangesAsync(
        MailMessageQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailMessageQueryStatus.AccountNotFound, null, [], [], 0);
        if (command.CheckAccountOnly)
            return new(MailMessageQueryStatus.Authorized, null, [], [], 0);
        var all = await JmapEmailQueryEngine.LoadAsync(database, content, account.InboxId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var filtered = JmapEmailQueryEngine.Filter(all, command.Criteria.Filter);
            var changes = await states.GetChangesAsync(account.InboxId, JmapConstants.EmailDataType,
                command.SinceState, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
            if (changes is null)
                return new(MailMessageQueryStatus.CannotCalculateChanges, null, [], [], 0);

            var ordered = JmapEmailQueryEngine.Sort(all, filtered, command.Criteria.Sort);
            if (command.Criteria.CollapseThreads)
                ordered = ordered.DistinctBy(item => item.ThreadId, StringComparer.Ordinal).ToList();
            var currentIds = ordered.Select(item => EmailId(item.Email.Id)).ToArray();
            var currentIdSet = currentIds.ToHashSet(StringComparer.Ordinal);
            var mutableFilter = JmapEmailQueryEngine.UsesMutableFilter(command.Criteria.Filter);
            var mutableSort = JmapEmailQueryEngine.UsesMutableSort(command.Criteria.Sort);
            var threadProperties = JmapEmailQueryEngine.UsesThreadProperties(
                command.Criteria.Filter, command.Criteria.Sort);
            var hasMembershipChanges = changes.Created.Count > 0 || changes.Destroyed.Count > 0;
            var resetQuery = threadProperties || command.Criteria.CollapseThreads
                && (hasMembershipChanges || mutableFilter && changes.Updated.Count > 0);

            string[] removed;
            HashSet<string> addedIds;
            if (resetQuery)
            {
                var createdIds = changes.Created.ToHashSet(StringComparer.Ordinal);
                var oldCandidates = mutableFilter
                    ? all.Select(item => EmailId(item.Email.Id))
                    : JmapEmailQueryEngine.Sort(all, filtered, command.Criteria.Sort)
                        .Select(item => EmailId(item.Email.Id));
                removed = oldCandidates.Where(id => !createdIds.Contains(id))
                    .Concat(changes.Destroyed).Distinct(StringComparer.Ordinal).ToArray();
                addedIds = currentIdSet;
            }
            else
            {
                var includeUpdates = mutableFilter || mutableSort;
                removed = changes.Destroyed.Concat(includeUpdates ? changes.Updated : [])
                    .Distinct(StringComparer.Ordinal).ToArray();
                addedIds = changes.Created.Concat(includeUpdates ? changes.Updated : [])
                    .Where(currentIdSet.Contains).ToHashSet(StringComparer.Ordinal);
            }
            var added = ordered.Select((item, index) => new MailMessageIndexedId(item.Email.Id, index))
                .Where(item => addedIds.Contains(EmailId(item.Id))).ToArray();
            if (command.MaxChanges is not null && removed.LongLength + added.LongLength > command.MaxChanges.Value)
                return new(MailMessageQueryStatus.TooManyChanges, null, [], [], 0);
            return new(MailMessageQueryStatus.Ok, changes.NewState, removed, added, currentIds.Length);
        }
        finally
        {
            foreach (var item in all) item.Dispose();
        }
    }

    private static List<JmapEmailQueryItem> FilterAndSort(
        IReadOnlyList<JmapEmailQueryItem> all, MailMessageQueryCriteria criteria)
    {
        var filtered = JmapEmailQueryEngine.Filter(all, criteria.Filter);
        var ordered = JmapEmailQueryEngine.Sort(all, filtered, criteria.Sort);
        return criteria.CollapseThreads
            ? ordered.DistinctBy(item => item.ThreadId, StringComparer.Ordinal).ToList()
            : ordered;
    }

    private static string EmailId(Guid id) => $"E{id:N}";
}
