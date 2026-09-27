using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class MailboxQueryMethod(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Mailbox/query";
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
                "position",
                "anchor",
                "anchorOffset",
                "limit",
                "calculateTotal",
                "sortAsTree",
                "filterAsTree")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "sortAsTree", false, out var sortAsTree)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "filterAsTree", false, out var filterAsTree)
            || !JmapMethodHelpers.TryGetQueryWindow(
                arguments,
                out var position,
                out var anchor,
                out var anchorOffset)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "limit", out var requestedLimit)
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
                filterAsTree,
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

        var ordered = JmapMailboxQueryEngine.Sort(allMailboxes, filtered, sort, sortAsTree);
        var ids = ordered.Select(mailbox => JmapId.Mailbox(mailbox.Id)).ToList();
        if (anchor is not null)
        {
            var anchorIndex = ids.IndexOf(anchor);
            if (anchorIndex < 0)
                return JmapMethodResponse.Error("anchorNotFound");
            position = Math.Min(
                JmapMethodHelpers.MaximumInt,
                Math.Max(0L, anchorIndex + anchorOffset));
        }
        else if (position < 0)
        {
            position = Math.Max(0L, ids.Count + position);
        }

        var enforcedLimit = JmapMethodHelpers.ClampToServerLimit(
            requestedLimit,
            environment.Jmap.MaxObjectsInGet);
        var pagePosition = position >= ids.Count ? ids.Count : checked((int)position);
        var page = pagePosition >= ids.Count
            ? []
            : ids.Skip(pagePosition).Take(enforcedLimit).ToList();
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["queryState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.MailboxDataType,
                cancellationToken).ConfigureAwait(false),
            ["canCalculateChanges"] = !sortAsTree && !filterAsTree,
            ["position"] = position,
            ["ids"] = JmapMethodHelpers.ToJsonArray(page),
        };
        if (calculateTotal)
            response["total"] = ids.Count;
        if (requestedLimit is null || requestedLimit > enforcedLimit)
            response["limit"] = enforcedLimit;
        return new JmapMethodResponse(Name, response);
    }
}
