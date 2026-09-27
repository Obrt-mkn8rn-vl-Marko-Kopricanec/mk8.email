using mk8.email.Contracts.Messaging;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class ContactCardQueryMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.FindContacts;
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments, "accountId", "filter", "sort", "position", "anchor",
                "anchorOffset", "limit", "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetQueryWindow(arguments, out var position, out var anchor, out var anchorOffset)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "limit", out var requestedLimit)
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
        var ids = JmapContactQueryEngine.Sort(filtered, comparators).Select(card => card.Id).ToList();
        if (anchor is not null)
        {
            var index = ids.IndexOf(anchor);
            if (index < 0)
                return JmapMethodResponse.Error("anchorNotFound");
            position = Math.Min(JmapMethodHelpers.MaximumInt, Math.Max(0L, index + anchorOffset));
        }
        else if (position < 0)
        {
            position = Math.Max(0L, ids.Count + position);
        }
        var limit = JmapMethodHelpers.ClampToServerLimit(requestedLimit, environment.Jmap.MaxObjectsInGet);
        var pagePosition = position >= ids.Count ? ids.Count : checked((int)position);
        var page = ids.Skip(pagePosition).Take(limit).ToArray();
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["queryState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.ContactCardDataType,
                cancellationToken).ConfigureAwait(false),
            ["canCalculateChanges"] = true,
            ["position"] = position,
            ["ids"] = JmapMethodHelpers.ToJsonArray(page),
        };
        if (calculateTotal) response["total"] = ids.Count;
        if (requestedLimit is null || requestedLimit > limit) response["limit"] = limit;
        return new JmapMethodResponse(Operation, response);
    }
}
