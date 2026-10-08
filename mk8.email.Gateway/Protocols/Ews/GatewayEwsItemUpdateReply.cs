using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemUpdateReply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MailMessageMutationResult Decode(MailOperationResult reply, MailMessageMutationCommand command,
        IReadOnlyDictionary<string, bool> expectedChanges)
    {
        if (reply.KnownEntities.Count != 0) throw Invalid();
        var data = ApplicationValueCodec.Decode(reply.Response.Data!) as JsonObject ?? throw Invalid();
        RequireMembers(data, "status", "oldState", "newState", "created", "updated", "destroyed");
        if (data["created"] is not JsonArray { Count: 0 } || data["destroyed"] is not JsonArray { Count: 0 }
            || data["updated"] is not JsonArray updated) throw Invalid();
        foreach (var node in updated)
        {
            if (node is not JsonObject item) throw Invalid();
            RequireMembers(item, "requestedId", "messageId", "failure");
            if (item["failure"] is JsonObject failure) RequireMembers(failure, "error", "description", "properties", "missingBlobIds");
        }
        var result = data.Deserialize<MailMessageMutationResult>(JsonOptions) ?? throw Invalid();
        if (!Enum.IsDefined(result.Status)) throw Invalid();
        if (result.Status != MailMessageMutationStatus.Ok)
        {
            if (result.OldState is not null || result.NewState is not null || result.Updated.Count != 0) throw Invalid();
            return result;
        }
        ValidateSuccess(result, command, expectedChanges);
        return result;
    }

    private static void ValidateSuccess(MailMessageMutationResult result, MailMessageMutationCommand command,
        IReadOnlyDictionary<string, bool> expectedChanges)
    {
        if (!string.Equals(result.OldState, command.IfInState, StringComparison.Ordinal)
            || string.IsNullOrEmpty(result.NewState) || result.NewState.Length > 256 || result.NewState.Any(char.IsControl)
            || result.Updated.Count != command.Updates.Count) throw Invalid();
        var changed = false;
        for (var index = 0; index < result.Updated.Count; index++)
        {
            var item = result.Updated[index];
            if (item is null || !string.Equals(item.RequestedId, command.Updates[index].RequestedId, StringComparison.Ordinal)
                || (item.MessageId is null) == (item.Failure is null)) throw Invalid();
            if (item.Failure is not null)
            {
                if (item.Failure.Error is not (MailMessageMutationError.NotFound or MailMessageMutationError.InvalidProperties or MailMessageMutationError.TooManyKeywords)
                    || item.Failure.Description is not null || item.Failure.Properties is not null || item.Failure.MissingBlobIds is not null) throw Invalid();
            }
            else
            {
                if (item.MessageId == Guid.Empty || !string.Equals(item.RequestedId, $"E{item.MessageId:N}", StringComparison.Ordinal)) throw Invalid();
                changed |= expectedChanges[item.RequestedId];
            }
        }
        // Setting an already-equal value is a legitimate success with no state
        // advance. A changed successful flag must not claim the old state.
        if (changed && string.Equals(result.OldState, result.NewState, StringComparison.Ordinal)) throw Invalid();
    }

    private static void RequireMembers(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key))) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("The EWS item update reply is invalid.");
}
