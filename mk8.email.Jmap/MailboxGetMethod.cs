using mk8.email.Contracts.Messaging;
using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class MailboxGetMethod(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.ReadFolders;
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
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

        var allMailboxes = await mailboxes.LoadAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
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

        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.MailboxDataType,
                cancellationToken).ConfigureAwait(false),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

}
