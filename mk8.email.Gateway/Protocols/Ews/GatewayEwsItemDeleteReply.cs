using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemDeleteReply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MailMessageMutationResult Decode(MailOperationResult reply, MailMessageMutationCommand command)
    {
        if (reply.KnownEntities.Count != 0) throw Invalid();
        var data = ApplicationValueCodec.Decode(reply.Response.Data!) as JsonObject ?? throw Invalid();
        RequireMembers(data, "status", "oldState", "newState", "created", "updated", "destroyed");
        if (data["created"] is not JsonArray { Count: 0 } || data["updated"] is not JsonArray { Count: 0 }
            || data["destroyed"] is not JsonArray destroyed) throw Invalid();
        foreach (var node in destroyed)
        {
            if (node is not JsonObject item) throw Invalid();
            RequireMembers(item, "requestedId", "messageId", "failure");
            if (item["failure"] is JsonObject failure)
                RequireMembers(failure, "error", "description", "properties", "missingBlobIds");
        }
        var result = data.Deserialize<MailMessageMutationResult>(JsonOptions) ?? throw Invalid();
        if (!Enum.IsDefined(result.Status)) throw Invalid();
        if (result.Status != MailMessageMutationStatus.Ok)
        {
            if (result.OldState is not null || result.NewState is not null || result.Destroyed.Count != 0) throw Invalid();
            return result;
        }
        ValidateSuccess(result, command);
        return result;
    }

    private static void ValidateSuccess(MailMessageMutationResult result, MailMessageMutationCommand command)
    {
        if (!string.Equals(result.OldState, command.IfInState, StringComparison.Ordinal)
            || string.IsNullOrEmpty(result.NewState) || result.NewState.Length > 256 || result.NewState.Any(char.IsControl)
            || result.Destroyed.Count != command.Destroys.Count) throw Invalid();
        if (result.Destroyed.Any(item => item?.MessageId is not null)
            && string.Equals(result.OldState, result.NewState, StringComparison.Ordinal)) throw Invalid();
        for (var index = 0; index < result.Destroyed.Count; index++)
        {
            var item = result.Destroyed[index];
            if (item is null || !string.Equals(item.RequestedId, command.Destroys[index].RequestedId, StringComparison.Ordinal)
                || (item.MessageId is null) == (item.Failure is null)) throw Invalid();
            if (item.Failure is not null)
            {
                // Only not-found is a possible per-item destroy refusal.
                if (item.Failure.Error != MailMessageMutationError.NotFound || item.Failure.Properties is not null
                    || item.Failure.MissingBlobIds is not null) throw Invalid();
            }
            else if (item.MessageId == Guid.Empty || !string.Equals(item.RequestedId, $"E{item.MessageId:N}", StringComparison.Ordinal))
                throw Invalid();
        }
    }

    private static void RequireMembers(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key))) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("The EWS item deletion reply is invalid.");
}
