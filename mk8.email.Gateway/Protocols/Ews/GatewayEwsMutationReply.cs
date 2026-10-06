using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsMutationReply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MailFolderMutationResult Decode(MailOperationResult reply, MailFolderMutationCommand command)
    {
        var data = ApplicationValueCodec.Decode(reply.Response.Data!) as JsonObject ?? throw Invalid();
        RequireMembers(data, "status", "oldState", "newState", "created", "updated", "destroyed");
        ValidateShape(data);
        var result = data.Deserialize<MailFolderMutationResult>(JsonOptions) ?? throw Invalid();
        if (!Enum.IsDefined(result.Status) || result.Created is null || result.Updated is null || result.Destroyed is null)
            throw Invalid();
        if (result.Status != MailFolderMutationStatus.Ok)
        {
            if (result.OldState is not null || result.NewState is not null || result.Created.Count != 0
                || result.Updated.Count != 0 || result.Destroyed.Count != 0 || reply.KnownEntities.Count != 0) throw Invalid();
            return result;
        }
        if (!string.Equals(result.OldState, command.IfInState, StringComparison.Ordinal)
            || string.IsNullOrEmpty(result.NewState) || result.NewState.Length > 256 || result.NewState.Any(char.IsControl)
            || result.Created.Count != command.Creates.Count || result.Updated.Count != command.Updates.Count
            || result.Destroyed.Count != command.Destroys.Count) throw Invalid();
        ValidateCreates(result, command, reply.KnownEntities);
        ValidateTargets(result.Updated.Select(item => (item.RequestedId, item.FolderId, item.Failure)),
            command.Updates.Select(item => item.RequestedId));
        ValidateTargets(result.Destroyed.Select(item => (item.RequestedId, item.FolderId, item.Failure)),
            command.Destroys.Select(item => item.RequestedId));
        return result;
    }

    private static void ValidateCreates(MailFolderMutationResult result, MailFolderMutationCommand command,
        IReadOnlyDictionary<string, string> knownEntities)
    {
        var ids = new HashSet<Guid>();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < result.Created.Count; index++)
        {
            var item = result.Created[index];
            var request = command.Creates[index];
            if (item is null || !string.Equals(item.CreationId, request.CreationId, StringComparison.Ordinal)
                || (item.Folder is null) == (item.Failure is null)) throw Invalid();
            if (item.Failure is not null) { ValidateFailure(item.Failure); continue; }
            var folder = item.Folder!;
            if (folder.Id == Guid.Empty || !ids.Add(folder.Id) || folder.Role is not null || folder.SortOrder != 0
                || folder.IsSubscribed || !string.Equals(folder.Name, request.Values!.Name, StringComparison.Ordinal)
                || Encoding.UTF8.GetByteCount(folder.Name) > 255
                || !string.Equals(folder.ParentId is null ? null : $"M{folder.ParentId:N}", request.Values.ParentReference, StringComparison.Ordinal))
                throw Invalid();
            expected.Add(item.CreationId, $"M{folder.Id:N}");
        }
        if (knownEntities.Count != expected.Count || expected.Any(item => !knownEntities.TryGetValue(item.Key, out var value)
            || !string.Equals(value, item.Value, StringComparison.Ordinal))) throw Invalid();
    }

    private static void ValidateTargets(IEnumerable<(string Id, Guid? FolderId, MailFolderMutationFailure? Failure)> outcomes,
        IEnumerable<string> requestedIds)
    {
        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in outcomes)
        {
            if (!requested.Contains(item.Id) || !seen.Add(item.Id) || (item.FolderId is null) == (item.Failure is null)) throw Invalid();
            if (item.Failure is not null) ValidateFailure(item.Failure);
            else if (item.FolderId == Guid.Empty || !string.Equals(item.Id, $"M{item.FolderId:N}", StringComparison.Ordinal)) throw Invalid();
        }
        if (!seen.SetEquals(requested)) throw Invalid();
    }

    private static void ValidateFailure(MailFolderMutationFailure failure)
    {
        if (!Enum.IsDefined(failure.Error) || failure.Error == MailFolderMutationError.None) throw Invalid();
    }

    private static void ValidateShape(JsonObject data)
    {
        foreach (var key in new[] { "created", "updated", "destroyed" })
        {
            if (data[key] is not JsonArray outcomes) throw Invalid();
            foreach (var node in outcomes)
            {
                if (node is not JsonObject item) throw Invalid();
                if (key is "created")
                {
                    RequireMembers(item, "creationId", "folder", "failure");
                    if (item["folder"] is JsonObject folder)
                        RequireMembers(folder, "id", "name", "parentId", "role", "sortOrder", "isSubscribed");
                }
                else RequireMembers(item, "requestedId", "folderId", "failure");
                if (item["failure"] is JsonObject failure) RequireMembers(failure, "error", "description", "properties");
            }
        }
    }

    private static void RequireMembers(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key))) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("The EWS folder mutation reply is invalid.");
}
