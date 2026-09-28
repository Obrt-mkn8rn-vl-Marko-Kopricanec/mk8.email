using System.Collections.Frozen;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayMailboxGetCodec
{
    private static readonly HashSet<string> Properties = new(
        ["id", "name", "parentId", "role", "sortOrder", "totalEmails", "unreadEmails",
            "totalThreads", "unreadThreads", "myRights", "isSubscribed"],
        StringComparer.Ordinal);

    internal sealed record Call(
        MailFolderReadCommand Command,
        string AccountId,
        IReadOnlyList<string>? RequestedIds,
        IReadOnlySet<string>? Properties,
        string? ArgumentFailure,
        int MaximumObjects);

    public static bool TryParse(
        JsonObject arguments,
        int maximumObjects,
        out Call? call,
        out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "ids" or "properties")))
        {
            failure = "invalidArguments";
            return false;
        }
        if (arguments["accountId"] is not JsonValue accountValue
            || !accountValue.TryGetValue<string>(out var accountId)
            || accountId is null)
        {
            failure = "invalidArguments";
            return false;
        }

        if (!TryParseId(accountId, 'A', out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }

        var propertiesValid = TryParseProperties(arguments, out var properties);
        var (requestedIds, folderIds, idsFailure) = ParseIds(arguments, maximumObjects);
        var argumentFailure = !propertiesValid ? "invalidArguments" : idsFailure;

        // For invalid properties or IDs, the legacy method checks ownership first.
        // The typed command authorizes only; Gateway then reports the argument error.
        if (argumentFailure is not null)
            folderIds = [];
        call = new(new(accountGuid, requestedIds is null && argumentFailure is null ? null : folderIds,
                argumentFailure is not null),
            accountId, requestedIds, properties, argumentFailure, maximumObjects);
        return true;
    }

    private static bool TryParseProperties(JsonObject arguments, out FrozenSet<string>? properties)
    {
        properties = null;
        if (!arguments.TryGetPropertyValue("properties", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var name)
                || name is null || !Properties.Contains(name))
            {
                return false;
            }
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static (string[]? RequestedIds, Guid[] FolderIds, string? Failure) ParseIds(
        JsonObject arguments, int maximumObjects)
    {
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return (null, [], null);
        if (node is not JsonArray array)
            return ([], [], "invalidArguments");

        var ids = new List<string>(array.Count);
        var folderIds = new List<Guid>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return (ids.ToArray(), [], "invalidArguments");
            ids.Add(id);
            if (TryParseId(id, 'M', out var folderId)
                && string.Equals(id, FormatId('M', folderId), StringComparison.Ordinal))
                folderIds.Add(folderId);
        }
        return (ids.ToArray(), folderIds.ToArray(), ids.Count > maximumObjects ? "requestTooLarge" : null);
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailFolderReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Folders is null)
            throw new InvalidOperationException("The Application returned an invalid folder read result.");
        if (result.Status == MailFolderReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.ArgumentFailure is not null)
            return Error(call.ArgumentFailure);
        if (result.Status == MailFolderReadStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.State is null || result.Folders.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned an incomplete folder read result.");

        var byId = new Dictionary<string, MailFolderSnapshot>(StringComparer.Ordinal);
        foreach (var folder in result.Folders)
        {
            var id = FormatId('M', folder.Id);
            if (folder.Id == Guid.Empty || folder.Name is null || !byId.TryAdd(id, folder))
                throw new InvalidOperationException("The Application returned invalid folder data.");
        }

        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var folder))
                list.Add(Build(folder, call.Properties));
            else
                notFound.Add(id);
        }
        return (MailOperationKind.ReadFolders, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject Build(MailFolderSnapshot folder, IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = FormatId('M', folder.Id) };
        if (Wants("name")) result["name"] = folder.Name;
        if (Wants("parentId")) result["parentId"] = folder.ParentId is null ? null : FormatId('M', folder.ParentId.Value);
        if (Wants("role")) result["role"] = folder.Role;
        if (Wants("sortOrder")) result["sortOrder"] = folder.SortOrder;
        if (Wants("totalEmails")) result["totalEmails"] = folder.TotalEmails;
        if (Wants("unreadEmails")) result["unreadEmails"] = folder.UnreadEmails;
        if (Wants("totalThreads")) result["totalThreads"] = folder.TotalThreads;
        if (Wants("unreadThreads")) result["unreadThreads"] = folder.UnreadThreads;
        if (Wants("myRights")) result["myRights"] = new JsonObject
        {
            ["mayReadItems"] = true,
            ["mayAddItems"] = true,
            ["mayRemoveItems"] = true,
            ["maySetSeen"] = true,
            ["maySetKeywords"] = true,
            ["mayCreateChild"] = true,
            ["mayRename"] = !folder.IsProtected,
            ["mayDelete"] = !folder.IsProtected,
            ["maySubmit"] = true,
        };
        if (Wants("isSubscribed")) result["isSubscribed"] = folder.IsSubscribed;
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });

    private static bool TryParseId(string value, char prefix, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == prefix
            && Guid.TryParseExact(value.AsSpan(1), "N", out id);
    }

    private static string FormatId(char prefix, Guid id) => $"{prefix}{id:N}";
}
