using mk8.email.Contracts.Messaging;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class ContactCardQueryChangesMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.FindContactChanges;
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments, "accountId", "filter", "sort", "sinceQueryState",
                "maxChanges", "upToId", "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || !JmapMethodHelpers.TryGetOptionalId(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken).ConfigureAwait(false));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken).ConfigureAwait(false);
        var cards = await contacts.LoadCardsAsync(account.UserId, false, cancellationToken).ConfigureAwait(false);
        if (!JmapContactQueryEngine.TryFilter(cards, arguments["filter"], out var filtered, out var filterError))
            return JmapMethodResponse.Error(filterError);
        if (!JmapContactQueryEngine.TryParseSort(arguments["sort"], out var comparators, out var sortError))
            return JmapMethodResponse.Error(sortError);
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.ContactCardDataType,
            sinceState,
            null,
            int.MaxValue,
            cancellationToken).ConfigureAwait(false);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");
        var currentIds = JmapContactQueryEngine.Sort(filtered, comparators)
            .Select(card => card.Id).ToList();
        var currentSet = currentIds.ToHashSet(StringComparer.Ordinal);
        var mutableQuery = comparators.Count > 0
            || JmapContactQueryEngine.IsFilterMutable(arguments["filter"]);
        var immutableQueryMatchesAll = !mutableQuery
            && JmapContactQueryEngine.ImmutableFilterMatchesAll(arguments["filter"]);
        IEnumerable<string> removedChanges = mutableQuery
            ? changes.Destroyed.Concat(changes.Updated)
            : immutableQueryMatchesAll ? changes.Destroyed : [];
        IEnumerable<string> addedChanges = mutableQuery
            ? changes.Created.Concat(changes.Updated)
            : immutableQueryMatchesAll ? changes.Created : [];
        var removed = removedChanges.Distinct(StringComparer.Ordinal).ToArray();
        var changedCurrent = addedChanges
            .Where(currentSet.Contains).ToHashSet(StringComparer.Ordinal);
        var added = currentIds.Select((id, index) => new { id, index })
            .Where(item => changedCurrent.Contains(item.id)).ToArray();
        if (maxChanges is not null && removed.LongLength + added.LongLength > maxChanges)
            return JmapMethodResponse.Error("tooManyChanges");
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldQueryState"] = sinceState,
            ["newQueryState"] = changes.NewState,
            ["removed"] = JmapMethodHelpers.ToJsonArray(removed),
            ["added"] = new JsonArray(added.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.id,
                ["index"] = item.index,
            }).ToArray()),
        };
        if (calculateTotal) response["total"] = filtered.Count;
        return new JmapMethodResponse(Operation, response);
    }
}
