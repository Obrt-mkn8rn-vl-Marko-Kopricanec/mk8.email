using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class MailboxQueryChangesMethod(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states) : IJmapMethod
{
    public string Name => "Mailbox/queryChanges";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "filter",
                "sort",
                "sinceQueryState",
                "maxChanges",
                "upToId",
                "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || !JmapMethodHelpers.TryGetOptionalId(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var allMailboxes = await mailboxes.LoadAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
        if (!JmapMailboxQueryEngine.TryFilter(
                allMailboxes,
                arguments["filter"],
                false,
                out var filtered,
                out var filterError))
        {
            return JmapMethodResponse.Error(filterError);
        }
        if (!JmapMailboxQueryEngine.TryParseSort(
                arguments["sort"],
                out var sort,
                out var sortError))
        {
            return JmapMethodResponse.Error(sortError);
        }

        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.MailboxDataType,
            sinceState,
            null,
            int.MaxValue,
            cancellationToken).ConfigureAwait(false);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");

        var currentIds = JmapMailboxQueryEngine.Sort(allMailboxes, filtered, sort, false)
            .Select(mailbox => JmapId.Mailbox(mailbox.Id))
            .ToList();
        var currentIdSet = currentIds.ToHashSet(StringComparer.Ordinal);
        var removed = changes.Destroyed
            .Concat(changes.Updated)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var changedCurrentIds = changes.Created
            .Concat(changes.Updated)
            .Where(currentIdSet.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var added = currentIds
            .Select((id, index) => new { Id = id, Index = index })
            .Where(item => changedCurrentIds.Contains(item.Id))
            .ToArray();
        if (maxChanges is not null && removed.LongLength + added.LongLength > maxChanges.Value)
            return JmapMethodResponse.Error("tooManyChanges");

        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldQueryState"] = sinceState,
            ["newQueryState"] = changes.NewState,
            ["removed"] = JmapMethodHelpers.ToJsonArray(removed),
            ["added"] = new JsonArray(added
                .Select(item => (JsonNode)new JsonObject
                {
                    ["id"] = item.Id,
                    ["index"] = item.Index,
                })
                .ToArray()),
        };
        if (calculateTotal)
            response["total"] = filtered.Count;
        return new JmapMethodResponse(Name, response);
    }
}
