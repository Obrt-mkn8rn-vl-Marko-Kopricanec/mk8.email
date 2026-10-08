using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCopyReply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MailCopyResult Decode(MailOperationResult reply, MailCopyCommand command,
        IReadOnlyDictionary<Guid, MailMessageSnapshot> sources)
    {
        var data = ApplicationValueCodec.Decode(reply.Response.Data!) as JsonObject ?? throw Invalid();
        RequireMembers(data, "status", "oldTargetState", "newTargetState", "items", "destroy");
        if (data["items"] is not JsonArray items) throw Invalid();
        if (data["destroy"] is not null)
        {
            if (!command.DestroyOriginal || data["destroy"] is not JsonObject destroy) throw Invalid();
            RequireMembers(destroy, "status", "oldState", "newState", "destroyed", "notFound");
        }
        foreach (var node in items)
        {
            if (node is not JsonObject item) throw Invalid();
            RequireMembers(item, "creationId", "error", "emailId", "storedThreadId", "size");
        }
        var result = data.Deserialize<MailCopyResult>(JsonOptions) ?? throw Invalid();
        if (!Enum.IsDefined(result.Status)) throw Invalid();
        if (result.Status != MailCopyStatus.Ok)
        {
            if (result.OldTargetState is not null || result.NewTargetState is not null || result.Items.Count != 0
                || reply.KnownEntities.Count != 0 || result.Destroy is not null) throw Invalid();
            return result;
        }
        if (!string.Equals(result.OldTargetState, command.IfInState, StringComparison.Ordinal)
            || string.IsNullOrEmpty(result.NewTargetState) || result.NewTargetState.Length > 256
            || result.NewTargetState.Any(char.IsControl) || result.Items.Count != command.Items.Count) throw Invalid();
        ValidateItems(result, command, sources, reply.KnownEntities);
        ValidateDestruction(result, command);
        return result;
    }

    private static void ValidateDestruction(MailCopyResult result, MailCopyCommand command)
    {
        var successful = result.Items.Select((item, index) => (item, index)).Where(pair => pair.item.Error == MailCopyItemError.None)
            .Select(pair => command.Items[pair.index].SourceEmailId!.Value).ToArray();
        if (!command.DestroyOriginal || successful.Length == 0)
        {
            if (result.Destroy is not null) throw Invalid();
            return;
        }
        var destroy = result.Destroy;
        if (command.SourceAccountId != command.TargetAccountId || command.DestroyFromIfInState is not null
            || destroy is null || destroy.Status != MailCopyDestroyStatus.Completed
            || !string.Equals(destroy.OldState, result.NewTargetState, StringComparison.Ordinal)
            || string.IsNullOrEmpty(destroy.NewState) || destroy.NewState.Length > 256 || destroy.NewState.Any(char.IsControl)
            || string.Equals(destroy.OldState, destroy.NewState, StringComparison.Ordinal)
            || string.Equals(result.OldTargetState, destroy.NewState, StringComparison.Ordinal)
            || destroy.Destroyed is null || destroy.NotFound is null || destroy.NotFound.Count != 0
            || successful.Distinct().Count() != successful.Length || destroy.Destroyed.Count != successful.Length
            || !destroy.Destroyed.SequenceEqual(successful)) throw Invalid();
    }

    private static void ValidateItems(MailCopyResult result, MailCopyCommand command,
        IReadOnlyDictionary<Guid, MailMessageSnapshot> sources, IReadOnlyDictionary<string, string> known)
    {
        var created = new HashSet<Guid>();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            var requested = command.Items[index];
            if (item is null || !string.Equals(item.CreationId, requested.CreationId, StringComparison.Ordinal)) throw Invalid();
            if (item.Error != MailCopyItemError.None)
            {
                if (item.Error is not (MailCopyItemError.NotFound or MailCopyItemError.InvalidMailbox or MailCopyItemError.TooLarge
                    or MailCopyItemError.OverQuota or MailCopyItemError.InvalidEmail)
                    || item.EmailId is not null || item.StoredThreadId is not null || item.Size is not null) throw Invalid();
                continue;
            }
            if (item.EmailId is null || item.EmailId == Guid.Empty || !created.Add(item.EmailId.Value)
                || sources.ContainsKey(item.EmailId.Value) || string.IsNullOrEmpty(item.StoredThreadId)
                || item.StoredThreadId.Length > 256 || item.StoredThreadId.Any(char.IsControl)
                || item.Size != sources[requested.SourceEmailId!.Value].RawSize) throw Invalid();
            expected.Add(item.CreationId, $"E{item.EmailId:N}");
        }
        if (expected.Count != known.Count || expected.Any(item => !known.TryGetValue(item.Key, out var id)
            || !string.Equals(id, item.Value, StringComparison.Ordinal))
            || expected.Count != 0 && string.Equals(result.OldTargetState, result.NewTargetState, StringComparison.Ordinal)) throw Invalid();
    }

    private static void RequireMembers(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key))) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("The EWS item copy reply is invalid.");
}
