using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailContactQueryService(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states) : IMailContactQueryService
{
    public async Task<MailContactQueryResult> QueryAsync(
        MailContactQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var accountStatus = await GetAccountStatusAsync(command.AccountId, command.AccountReferenceParseable,
            command.AccountReferenceEligible, user, cancellationToken).ConfigureAwait(false);
        if (accountStatus != MailContactQueryStatus.Ok)
            return new(accountStatus, null, 0, [], 0);
        if (command.CheckAccountOnly)
            return new(MailContactQueryStatus.Authorized, null, 0, [], 0);
        var ordered = await LoadSortedAsync(command.Criteria, user, cancellationToken)
            .ConfigureAwait(false);
        var ids = ordered.Select(card => card.Resource.Id).ToArray();
        var position = command.Position;
        if (command.AnchorId is { } anchor)
        {
            if (!command.AnchorCanMatch)
                return new(MailContactQueryStatus.AnchorNotFound, null, 0, [], 0);
            var index = Array.IndexOf(ids, anchor);
            if (index < 0)
                return new(MailContactQueryStatus.AnchorNotFound, null, 0, [], 0);
            position = Math.Min(9_007_199_254_740_991L, Math.Max(0L, index + command.AnchorOffset));
        }
        else if (position < 0)
            position = Math.Max(0L, ids.Length + position);
        var pagePosition = position >= ids.Length ? ids.Length : checked((int)position);
        var page = pagePosition >= ids.Length ? [] : ids.Skip(pagePosition).Take(command.Limit).ToArray();
        var state = await states.GetStateAsync(command.AccountId, JmapConstants.ContactCardDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailContactQueryStatus.Ok, state, position, page, ids.Length);
    }

    public async Task<MailContactQueryChangesResult> QueryChangesAsync(
        MailContactQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var accountStatus = await GetAccountStatusAsync(command.AccountId, command.AccountReferenceParseable,
            command.AccountReferenceEligible, user, cancellationToken).ConfigureAwait(false);
        if (accountStatus != MailContactQueryStatus.Ok)
            return new(accountStatus, null, [], [], 0);
        if (command.CheckAccountOnly)
            return new(MailContactQueryStatus.Authorized, null, [], [], 0);
        var ordered = await LoadSortedAsync(command.Criteria, user, cancellationToken)
            .ConfigureAwait(false);
        var changes = await states.GetChangesAsync(command.AccountId, JmapConstants.ContactCardDataType,
            command.SinceState, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (changes is null)
            return new(MailContactQueryStatus.CannotCalculateChanges, null, [], [], 0);
        var currentSet = ordered.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
        var mutableQuery = command.Criteria.Sort.Count > 0
            || JmapContactQueryEngine.IsFilterMutable(command.Criteria.Filter);
        var immutableQueryMatchesAll = !mutableQuery
            && JmapContactQueryEngine.ImmutableFilterMatchesAll(command.Criteria.Filter);
        IEnumerable<string> removedChanges = mutableQuery
            ? changes.Destroyed.Concat(changes.Updated)
            : immutableQueryMatchesAll ? changes.Destroyed : [];
        IEnumerable<string> addedChanges = mutableQuery
            ? changes.Created.Concat(changes.Updated)
            : immutableQueryMatchesAll ? changes.Created : [];
        var removed = removedChanges.Distinct(StringComparer.Ordinal).ToArray();
        var changedCurrent = addedChanges.Where(currentSet.Contains).ToHashSet(StringComparer.Ordinal);
        var added = ordered.Select((card, index) => new MailContactIndexedId(card.Resource.Id, index))
            .Where(item => changedCurrent.Contains(CardId(item.Id))).ToArray();
        if (command.MaxChanges is not null && removed.LongLength + added.LongLength > command.MaxChanges.Value)
            return new(MailContactQueryStatus.TooManyChanges, null, [], [], 0);
        return new(MailContactQueryStatus.Ok, changes.NewState, removed, added, ordered.Count);
    }

    private async Task<MailContactQueryStatus> GetAccountStatusAsync(
        Guid accountId,
        bool parseable,
        bool eligible,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = parseable
            ? await accounts.GetAccountByInboxIdAsync(user, accountId, cancellationToken).ConfigureAwait(false)
            : null;
        if (account is null)
            return MailContactQueryStatus.AccountNotFound;
        var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        if (!eligible || available.Count == 0 || available[0].InboxId != account.InboxId)
            return MailContactQueryStatus.AccountNotSupported;
        return MailContactQueryStatus.Ok;
    }

    private async Task<IReadOnlyList<JmapContactCardView>> LoadSortedAsync(
        MailContactQueryCriteria criteria,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (criteria?.Sort is null)
            throw new InvalidOperationException("The contact query criteria are incomplete.");
        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken).ConfigureAwait(false);
        var cards = await contacts.LoadCardsAsync(user.Id, false, cancellationToken).ConfigureAwait(false);
        return JmapContactQueryEngine.Sort(JmapContactQueryEngine.Filter(cards, criteria.Filter), criteria.Sort);
    }

    private static string CardId(Guid id) => $"C{id:N}";
}
