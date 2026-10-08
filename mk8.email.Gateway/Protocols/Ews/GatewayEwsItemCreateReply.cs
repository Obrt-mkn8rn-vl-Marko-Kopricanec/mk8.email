using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCreateReply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MailImportResult Decode(MailOperationResult reply, MailImportCommand command,
        IReadOnlyDictionary<string, int> sizes)
    {
        var data = ApplicationValueCodec.Decode(reply.Response.Data!) as JsonObject ?? throw Invalid();
        RequireMembers(data, "status", "oldState", "newState", "items");
        if (data["items"] is not JsonArray items) throw Invalid();
        foreach (var node in items)
        {
            if (node is not JsonObject item) throw Invalid();
            RequireMembers(item, "creationId", "error", "emailId", "storedThreadId", "size");
        }
        var result = data.Deserialize<MailImportResult>(JsonOptions) ?? throw Invalid();
        if (!Enum.IsDefined(result.Status)) throw Invalid();
        if (result.Status != MailImportStatus.Ok)
        {
            if (result.OldState is not null || result.NewState is not null || result.Items.Count != 0 || reply.KnownEntities.Count != 0) throw Invalid();
            return result;
        }
        if (!string.Equals(result.OldState, command.IfInState, StringComparison.Ordinal)
            || string.IsNullOrEmpty(result.NewState) || result.NewState.Length > 256 || result.NewState.Any(char.IsControl)
            || result.Items.Count != command.Items.Count) throw Invalid();
        ValidateItems(result, command, sizes, reply.KnownEntities);
        return result;
    }

    private static void ValidateItems(MailImportResult result, MailImportCommand command,
        IReadOnlyDictionary<string, int> sizes, IReadOnlyDictionary<string, string> known)
    {
        var ids = new HashSet<Guid>();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            if (item is null || !string.Equals(item.CreationId, command.Items[index].CreationId, StringComparison.Ordinal)) throw Invalid();
            if (item.Error != MailImportItemError.None)
            {
                if (item.Error is not (MailImportItemError.MissingBlob or MailImportItemError.InvalidMailbox or MailImportItemError.TooLarge
                    or MailImportItemError.OverQuota or MailImportItemError.InvalidEmail)
                    || item.EmailId is not null || item.StoredThreadId is not null || item.Size is not null) throw Invalid();
                continue;
            }
            if (item.EmailId is null || item.EmailId == Guid.Empty || !ids.Add(item.EmailId.Value)
                || string.IsNullOrEmpty(item.StoredThreadId) || item.StoredThreadId.Length > 256 || item.StoredThreadId.Any(char.IsControl)
                || item.Size != sizes[item.CreationId]) throw Invalid();
            expected.Add(item.CreationId, $"E{item.EmailId:N}");
        }
        if (expected.Count != known.Count || expected.Any(item => !known.TryGetValue(item.Key, out var id)
            || !string.Equals(id, item.Value, StringComparison.Ordinal))
            || expected.Count != 0 && string.Equals(result.OldState, result.NewState, StringComparison.Ordinal)) throw Invalid();
    }

    private static void RequireMembers(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key))) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("The EWS item creation reply is invalid.");
}
