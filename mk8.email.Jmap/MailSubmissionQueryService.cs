using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailSubmissionQueryService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states) : IMailSubmissionQueryService
{
    public async Task<MailSubmissionQueryResult> QueryAsync(
        MailSubmissionQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailSubmissionQueryStatus.AccountNotFound, null, 0, [], 0);
        if (command.CheckAccountOnly)
            return new(MailSubmissionQueryStatus.Authorized, null, 0, [], 0);
        var sorted = await LoadSortedAsync(account.InboxId, command.Criteria, cancellationToken)
            .ConfigureAwait(false);
        var ids = sorted.Select(item => item.Id).ToArray();
        var position = command.Position;
        if (command.AnchorId is { } anchor)
        {
            if (!command.AnchorCanMatch)
                return new(MailSubmissionQueryStatus.AnchorNotFound, null, 0, [], 0);
            var index = Array.IndexOf(ids, anchor);
            if (index < 0)
                return new(MailSubmissionQueryStatus.AnchorNotFound, null, 0, [], 0);
            position = Math.Min(9_007_199_254_740_991L, Math.Max(0L, index + command.AnchorOffset));
        }
        else if (position < 0)
            position = Math.Max(0L, ids.Length + position);
        var pagePosition = position >= ids.Length ? ids.Length : checked((int)position);
        var page = pagePosition >= ids.Length ? [] : ids.Skip(pagePosition).Take(command.Limit).ToArray();
        var state = await states.GetStateAsync(account.InboxId,
            JmapConstants.EmailSubmissionDataType, cancellationToken).ConfigureAwait(false);
        return new(MailSubmissionQueryStatus.Ok, state, position, page, ids.Length);
    }

    public async Task<MailSubmissionQueryChangesResult> QueryChangesAsync(
        MailSubmissionQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailSubmissionQueryStatus.AccountNotFound, null, [], [], 0);
        if (command.CheckAccountOnly)
            return new(MailSubmissionQueryStatus.Authorized, null, [], [], 0);
        var sorted = await LoadSortedAsync(account.InboxId, command.Criteria, cancellationToken)
            .ConfigureAwait(false);
        var changes = await states.GetChangesAsync(account.InboxId,
            JmapConstants.EmailSubmissionDataType, command.SinceState, null, int.MaxValue,
            cancellationToken).ConfigureAwait(false);
        if (changes is null)
            return new(MailSubmissionQueryStatus.CannotCalculateChanges, null, [], [], 0);
        var currentIds = sorted.Select(item => item.Id).ToArray();
        var createdIds = changes.Created.ToHashSet(StringComparer.Ordinal);
        var added = currentIds.Select((id, index) => new MailSubmissionIndexedId(id, index))
            .Where(item => createdIds.Contains(SubmissionId(item.Id))).ToArray();
        var removed = changes.Destroyed.Distinct(StringComparer.Ordinal).ToArray();
        if (command.MaxChanges is not null && removed.LongLength + added.LongLength > command.MaxChanges.Value)
            return new(MailSubmissionQueryStatus.TooManyChanges, null, [], [], 0);
        return new(MailSubmissionQueryStatus.Ok, changes.NewState, removed, added, currentIds.Length);
    }

    private async Task<IReadOnlyList<JmapEmailSubmissionDB>> LoadSortedAsync(
        Guid accountId,
        MailSubmissionQueryCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (criteria is null || criteria.Sort is null)
            throw new InvalidOperationException("The submission query criteria are incomplete.");
        var all = await database.JmapEmailSubmissions.AsNoTracking()
            .Where(item => item.AccountId == accountId).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return MailSubmissionQueryEngine.Sort(all.Where(MailSubmissionQueryEngine.BuildPredicate(criteria.Filter)),
            criteria.Sort);
    }

    private static string SubmissionId(Guid id) => $"S{id:N}";
}
