using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapChangesCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonObject Render(JmapApplicationChanges changes)
    {
        var changed = new JsonObject();
        foreach (var account in changes.AccountStates)
        {
            var types = new JsonObject();
            foreach (var item in account.Value)
                types[item.Key] = item.Value;
            changed[account.Key] = types;
        }
        return GatewayJmapJson.SanitizeResponse(new JsonObject
        {
            ["@type"] = "StateChange",
            ["changed"] = changed,
        });
    }

    public static byte[] EncodePush(JmapPushMessage message)
    {
        JsonObject value;
        if (message.Changes is not null && message.SubscriptionId is null && message.VerificationCode is null)
            value = Render(message.Changes);
        else if (message.Changes is null && message.SubscriptionId is not null && message.VerificationCode is not null)
            value = GatewayJmapJson.SanitizeResponse(new JsonObject
            {
                ["@type"] = "PushVerification",
                ["pushSubscriptionId"] = message.SubscriptionId,
                ["verificationCode"] = message.VerificationCode,
            });
        else
            throw new ArgumentException("A JMAP push must contain either a verification or a change notification.", nameof(message));
        return Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions));
    }
}
