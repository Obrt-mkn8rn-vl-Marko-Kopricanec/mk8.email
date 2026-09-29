using System.Collections.Frozen;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayPushSubscriptionGetCodec
{
    private static readonly HashSet<string> AllowedProperties = new(
        ["id", "deviceClientId", "url", "keys", "verificationCode", "expires", "types"],
        StringComparer.Ordinal);

    internal sealed record Call(
        MailPushSubscriptionReadCommand Command,
        IReadOnlyList<string>? RequestedIds,
        IReadOnlySet<string>? Properties,
        int MaximumObjects);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("ids" or "properties"))
            || !TryParseProperties(arguments, out var properties)
            || !TryParseIds(arguments, out var requestedIds, out var subscriptionIds))
        {
            failure = "invalidArguments";
            return false;
        }
        if (properties is not null && (properties.Contains("url") || properties.Contains("keys")))
        {
            failure = "forbidden";
            return false;
        }
        if (requestedIds?.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        call = new(new(requestedIds is null ? null : subscriptionIds),
            requestedIds, properties, maximumObjects);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailPushSubscriptionReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Subscriptions is null)
            throw new InvalidOperationException("The Application returned an invalid push-subscription read result.");
        if (result.Status == MailPushSubscriptionReadStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.Subscriptions.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned too many push subscriptions.");

        var byId = new Dictionary<string, MailPushSubscriptionSnapshot>(StringComparer.Ordinal);
        foreach (var subscription in result.Subscriptions)
        {
            if (subscription.Id == Guid.Empty || subscription.DeviceClientId is null
                || !byId.TryAdd(FormatId(subscription.Id), subscription))
                throw new InvalidOperationException("The Application returned invalid push-subscription data.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var subscription))
                list.Add(Build(subscription, call.Properties));
            else
                notFound.Add(id);
        }
        return (MailOperationKind.ReadNotificationSubscriptions, new JsonObject
        {
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject Build(MailPushSubscriptionSnapshot subscription, IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = FormatId(subscription.Id) };
        if (Wants("deviceClientId")) result["deviceClientId"] = subscription.DeviceClientId;
        if (Wants("verificationCode")) result["verificationCode"] = subscription.VerificationCode;
        if (Wants("expires")) result["expires"] = GatewayJmapDateCodec.FormatUtc(subscription.ExpiresAt);
        if (Wants("types"))
        {
            var types = subscription.Types is null ? null : new JsonArray();
            if (types is not null)
            {
                foreach (var type in subscription.Types!)
                    types.Add(type);
            }
            result["types"] = types;
        }
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
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
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name)
                || name is null || !AllowedProperties.Contains(name))
                return false;
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static bool TryParseIds(JsonObject arguments, out string[]? ids, out Guid[] subscriptionIds)
    {
        ids = null;
        subscriptionIds = [];
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var requested = new List<string>(array.Count);
        var parsed = new List<Guid>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            requested.Add(id);
            if (id.Length == 33 && id[0] == 'P'
                && Guid.TryParseExact(id.AsSpan(1), "N", out var subscriptionId)
                && string.Equals(id, FormatId(subscriptionId), StringComparison.Ordinal))
                parsed.Add(subscriptionId);
        }
        ids = requested.ToArray();
        subscriptionIds = parsed.ToArray();
        return true;
    }

    private static string FormatId(Guid id) => $"P{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
