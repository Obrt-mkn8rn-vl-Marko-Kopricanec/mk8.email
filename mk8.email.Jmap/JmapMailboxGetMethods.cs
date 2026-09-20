using System.Text.Json.Nodes;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

internal static class JmapMailboxJson
{
    public static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        [
            "id",
            "name",
            "parentId",
            "role",
            "sortOrder",
            "totalEmails",
            "unreadEmails",
            "totalThreads",
            "unreadThreads",
            "myRights",
            "isSubscribed",
        ],
        StringComparer.Ordinal);

    public static JsonObject Build(
        JmapMailboxView mailbox,
        IReadOnlySet<string>? properties = null)
    {
        var result = new JsonObject { ["id"] = JmapId.Mailbox(mailbox.Id) };
        if (Wants("name")) result["name"] = mailbox.Name;
        if (Wants("parentId")) result["parentId"] = mailbox.ParentId is null
            ? null
            : JmapId.Mailbox(mailbox.ParentId.Value);
        if (Wants("role")) result["role"] = mailbox.Role;
        if (Wants("sortOrder")) result["sortOrder"] = mailbox.SortOrder;
        if (Wants("totalEmails")) result["totalEmails"] = mailbox.TotalEmails;
        if (Wants("unreadEmails")) result["unreadEmails"] = mailbox.UnreadEmails;
        if (Wants("totalThreads")) result["totalThreads"] = mailbox.TotalThreads;
        if (Wants("unreadThreads")) result["unreadThreads"] = mailbox.UnreadThreads;
        if (Wants("myRights"))
        {
            result["myRights"] = new JsonObject
            {
                ["mayReadItems"] = true,
                ["mayAddItems"] = true,
                ["mayRemoveItems"] = true,
                ["maySetSeen"] = true,
                ["maySetKeywords"] = true,
                ["mayCreateChild"] = true,
                ["mayRename"] = !mailbox.IsProtected,
                ["mayDelete"] = !mailbox.IsProtected,
                ["maySubmit"] = true,
            };
        }
        if (Wants("isSubscribed")) result["isSubscribed"] = mailbox.IsSubscribed;
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }
}

internal sealed class MailboxGetMethod(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Mailbox/get";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");

        if (!JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var propertyList))
            return JmapMethodResponse.Error("invalidArguments");
        IReadOnlySet<string>? properties = propertyList?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !JmapMailboxJson.Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");

        if (!JmapMethodHelpers.TryGetIdArray(
                arguments,
                "ids",
                true,
                out var requestedIds))
            return JmapMethodResponse.Error("invalidArguments");
        if (requestedIds is { Count: > 0 }
            && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
        {
            return JmapMethodResponse.Error("requestTooLarge");
        }

        var allMailboxes = await mailboxes.LoadAsync(account.InboxId, cancellationToken);
        if (requestedIds is null && allMailboxes.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");

        var byId = allMailboxes.ToDictionary(mailbox => JmapId.Mailbox(mailbox.Id), StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        var ids = requestedIds ?? byId.Keys.ToArray();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var mailbox))
                list.Add(JmapMailboxJson.Build(mailbox, properties));
            else
                notFound.Add(id);
        }

        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.MailboxDataType,
                cancellationToken),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

}

internal sealed class MailboxChangesMethod(
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Mailbox/changes";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "sinceState", "maxChanges")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.MailboxDataType,
            sinceState,
            maxChanges,
            environment.Jmap.MaxObjectsInGet,
            cancellationToken);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");

        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = changes.OldState,
            ["newState"] = changes.NewState,
            ["hasMoreChanges"] = changes.HasMoreChanges,
            ["created"] = JmapMethodHelpers.ToJsonArray(changes.Created),
            ["updated"] = JmapMethodHelpers.ToJsonArray(changes.Updated),
            ["destroyed"] = JmapMethodHelpers.ToJsonArray(changes.Destroyed),
            ["updatedProperties"] = null,
        });
    }
}
