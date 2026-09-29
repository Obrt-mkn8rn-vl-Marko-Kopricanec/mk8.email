using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayMailboxSetCodec
{
    internal sealed record Call(
        MailFolderMutationCommand Command,
        string AccountId,
        IReadOnlyDictionary<string, JsonObject> CreateRequests);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ifInState" or "create"
                or "update" or "destroy" or "onDestroyRemoveEmails"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
            || !TryRemoveEmails(arguments, out var removeEmails)
            || !TryMap(arguments, "create", true, out var creates)
            || !TryMap(arguments, "update", false, out var updates)
            || !TryDestroy(arguments, out var destroys))
        {
            failure = "invalidArguments";
            return false;
        }
        if (creates.Count + updates.Count + destroys.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        var accountIdValue = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var parsed)
                ? parsed : Guid.Empty;
        var createCommands = creates.Select(item => new MailFolderCreate(item.Key,
            ApplicationValueCodec.Encode(item.Value))).ToArray();
        var updateCommands = updates.Select(item => new MailFolderUpdate(item.Key,
            ApplicationValueCodec.Encode(item.Value))).ToArray();
        var destroyCommands = destroys.Distinct(StringComparer.Ordinal)
            .Select(item => new MailFolderDestroy(item)).ToArray();
        call = new(new(accountIdValue, ifInState, removeEmails, createCommands,
            updateCommands, destroyCommands), accountId, creates);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(
        Call call, MailFolderMutationResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Created is null || result.Updated is null || result.Destroyed is null)
            throw new InvalidOperationException("The Application returned an invalid folder mutation.");
        if (result.Status == MailFolderMutationStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailFolderMutationStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status != MailFolderMutationStatus.Ok || result.OldState is null || result.NewState is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete folder mutation outcomes.");
        var (created, notCreated) = RenderCreated(call, result.Created);
        var (updated, notUpdated) = RenderUpdated(call, result.Updated);
        var (destroyed, notDestroyed) = RenderDestroyed(call, result.Destroyed);
        return (MailOperationKind.MutateFolders, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldState"] = result.OldState,
            ["newState"] = result.NewState,
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static (JsonObject Created, JsonObject NotCreated) RenderCreated(
        Call call, IReadOnlyList<MailFolderCreateOutcome> outcomes)
    {
        var created = new JsonObject();
        var notCreated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            var expected = call.Command.Creates[index];
            if (item is null || !string.Equals(item.CreationId, expected.CreationId, StringComparison.Ordinal)
                || (item.Folder is null) == (item.Failure is null))
                throw new InvalidOperationException("The Application returned an inconsistent folder creation.");
            if (item.Failure is not null)
            {
                notCreated[item.CreationId] = ItemError(item.Failure);
                continue;
            }
            var folder = item.Folder!;
            if (folder.Id == Guid.Empty || folder.Name is null
                || folder.SortOrder is < 0 or > int.MaxValue)
                throw new InvalidOperationException("The Application returned an invalid created folder.");
            var request = call.CreateRequests[item.CreationId];
            var properties = new HashSet<string>(
                ["totalEmails", "unreadEmails", "totalThreads", "unreadThreads", "myRights"],
                StringComparer.Ordinal);
            if (!request.ContainsKey("parentId")) properties.Add("parentId");
            if (!request.ContainsKey("role")) properties.Add("role");
            if (!request.ContainsKey("sortOrder")) properties.Add("sortOrder");
            if (!request.ContainsKey("isSubscribed")) properties.Add("isSubscribed");
            if (!string.Equals(request["name"]?.GetValue<string>(), folder.Name, StringComparison.Ordinal))
                properties.Add("name");
            var view = new MailFolderSnapshot(folder.Id, folder.Name, folder.ParentId, folder.Role,
                folder.SortOrder, folder.IsSubscribed, 0, 0, 0, 0,
                folder.Role is "inbox" or "sent" or "drafts" or "trash" or "junk");
            created[item.CreationId] = GatewayMailboxGetCodec.Build(view, properties);
        }
        return (created, notCreated);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdated(
        Call call, IReadOnlyList<MailFolderUpdateOutcome> outcomes)
    {
        var updated = new JsonObject();
        var notUpdated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            var expected = call.Command.Updates[index];
            if (item is null || !string.Equals(item.RequestedId, expected.RequestedId, StringComparison.Ordinal)
                || (item.FolderId is null) == (item.Failure is null))
                throw new InvalidOperationException("The Application returned an inconsistent folder update.");
            if (item.Failure is not null) notUpdated[item.RequestedId] = ItemError(item.Failure);
            else updated[FormatId(item.FolderId!.Value)] = null;
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroyed(
        Call call, IReadOnlyList<MailFolderDestroyOutcome> outcomes)
    {
        var destroyed = new JsonArray();
        var notDestroyed = new JsonObject();
        var remaining = call.Command.Destroys.Select(item => item.RequestedId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in outcomes)
        {
            if (item is null || !remaining.Remove(item.RequestedId)
                || (item.FolderId is null) == (item.Failure is null))
                throw new InvalidOperationException("The Application returned an inconsistent folder deletion.");
            if (item.Failure is not null) notDestroyed[item.RequestedId] = ItemError(item.Failure);
            else destroyed.Add(FormatId(item.FolderId!.Value));
        }
        if (remaining.Count > 0)
            throw new InvalidOperationException("The Application omitted a folder deletion result.");
        return (destroyed, notDestroyed);
    }

    private static JsonObject ItemError(MailFolderMutationFailure failure)
    {
        if (failure is null || !Enum.IsDefined(failure.Error)
            || failure.Error == MailFolderMutationError.None)
            throw new InvalidOperationException("The Application returned an invalid folder error.");
        var type = failure.Error switch
        {
            MailFolderMutationError.InvalidProperties => "invalidProperties",
            MailFolderMutationError.InvalidPatch => "invalidPatch",
            MailFolderMutationError.NotFound => "notFound",
            MailFolderMutationError.Forbidden => "forbidden",
            MailFolderMutationError.MailboxHasChild => "mailboxHasChild",
            MailFolderMutationError.MailboxHasEmail => "mailboxHasEmail",
            _ => throw new InvalidOperationException("The Application returned an unknown folder error."),
        };
        var result = new JsonObject { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(failure.Description)) result["description"] = failure.Description;
        if (failure.Properties is not null)
        {
            var properties = new JsonArray();
            foreach (var property in failure.Properties) properties.Add(property);
            result["properties"] = properties;
        }
        return result;
    }

    private static bool TryMap(JsonObject arguments, string name, bool creation,
        out Dictionary<string, JsonObject> result)
    {
        result = new(StringComparer.Ordinal);
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null) return true;
        if (node is not JsonObject map || map.Any(item => item.Value is not JsonObject
            || !IsReference(item.Key, creation))) return false;
        result = map.ToDictionary(item => item.Key, item => (JsonObject)item.Value!, StringComparer.Ordinal);
        return true;
    }

    private static bool TryDestroy(JsonObject arguments, out IReadOnlyList<string> values)
    {
        values = [];
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null) return true;
        if (node is not JsonArray array || array.Any(item => item is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var id) || id is null || !IsReference(id, false))) return false;
        values = array.Select(item => item!.GetValue<string>()).ToArray();
        return true;
    }

    private static bool TryOptionalString(JsonObject arguments, string name, out string? value)
    {
        value = null;
        return !arguments.TryGetPropertyValue(name, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue<string>(out value);
    }

    private static bool TryRemoveEmails(JsonObject arguments, out bool value)
    {
        value = false;
        return !arguments.TryGetPropertyValue("onDestroyRemoveEmails", out var node)
            || node is JsonValue scalar && scalar.TryGetValue<bool>(out value);
    }

    private static bool IsReference(string value, bool creation) =>
        GatewayJmapBatchCodec.IsId(value)
        || !creation && value.Length > 1 && value[0] == '#'
            && GatewayJmapBatchCodec.IsId(value[1..]);

    private static string FormatId(Guid id) => $"M{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
