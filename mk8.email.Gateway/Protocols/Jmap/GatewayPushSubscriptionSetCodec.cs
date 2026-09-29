using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayPushSubscriptionSetCodec
{
    internal sealed record Call(
        MailPushSubscriptionMutationCommand Command,
        IReadOnlyDictionary<string, string> KnownEntities,
        IReadOnlyDictionary<string, JsonObject> CreateRequests,
        IReadOnlyDictionary<string, JsonObject> UpdateErrors);

    public static bool TryParse(JsonObject arguments, IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("create" or "update" or "destroy"))
            || !TryMap(arguments, "create", creation: true, out var creates)
            || !TryMap(arguments, "update", creation: false, out var updates)
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
        var requests = creates.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var updateErrors = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var createCommands = creates.Select(item => new MailPushSubscriptionCreate(item.Key,
            TryCreate(item.Value, out var values) ? values : null)).ToArray();
        var updateCommands = updates.Select(item =>
        {
            var patch = TryPatch(item.Value, out var issue);
            if (issue is not null) updateErrors[item.Key] = issue;
            return new MailPushSubscriptionUpdate(item.Key, ParseTarget(item.Key, knownEntities), patch);
        }).ToArray();
        var destroyCommands = destroys.Distinct(StringComparer.Ordinal)
            .Select(id => new MailPushSubscriptionDestroy(id, ParseTarget(id, knownEntities))).ToArray();
        call = new(new(createCommands, updateCommands, destroyCommands), knownEntities, requests, updateErrors);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call,
        MailPushSubscriptionMutationResult result)
    {
        if (result is null || result.Created is null || result.Updated is null || result.Destroyed is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete push mutation outcomes.");
        var (created, notCreated, createdIds) = RenderCreated(call, result.Created);
        var (updated, notUpdated) = RenderUpdated(call, result.Updated, createdIds);
        var (destroyed, notDestroyed) = RenderDestroyed(call, result.Destroyed, createdIds);
        return (MailOperationKind.MutateNotificationSubscriptions, new JsonObject
        {
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static (JsonObject Created, JsonObject NotCreated, Dictionary<string, string> CreatedIds)
        RenderCreated(Call call, IReadOnlyList<MailPushSubscriptionCreateOutcome> outcomes)
    {
        var created = new JsonObject();
        var notCreated = new JsonObject();
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            var command = call.Command.Creates[index];
            if (outcome is null || !Enum.IsDefined(outcome.Error)
                || !string.Equals(outcome.CreationId, command.CreationId, StringComparison.Ordinal)
                || outcome.Error == MailPushSubscriptionMutationError.None
                    != (outcome.SubscriptionId is { } id && id != Guid.Empty && outcome.ExpiresAt is not null))
                throw new InvalidOperationException("The Application returned inconsistent push creation.");
            if (outcome.Error != MailPushSubscriptionMutationError.None)
            {
                notCreated[outcome.CreationId] = ItemError(outcome.Error == MailPushSubscriptionMutationError.Skipped
                    ? MailPushSubscriptionMutationError.InvalidProperties : outcome.Error);
                continue;
            }
            var wireId = FormatId(outcome.SubscriptionId!.Value);
            createdIds[outcome.CreationId] = wireId;
            var request = call.CreateRequests[outcome.CreationId];
            var response = new JsonObject { ["id"] = wireId };
            if (!request.ContainsKey("keys")) response["keys"] = RenderKeys(outcome.Keys);
            if (!request.TryGetPropertyValue("expires", out var requested)
                || requested is not JsonValue scalar
                || !scalar.TryGetValue<string>(out var text)
                || !GatewayJmapDateCodec.TryParseUtc(text, out var parsed)
                || parsed != outcome.ExpiresAt!.Value)
                response["expires"] = GatewayJmapDateCodec.FormatUtc(outcome.ExpiresAt!.Value);
            if (!request.ContainsKey("types")) response["types"] = RenderTypes(outcome.Types);
            created[outcome.CreationId] = response;
        }
        return (created, notCreated, createdIds);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdated(Call call,
        IReadOnlyList<MailPushSubscriptionUpdateOutcome> outcomes,
        IReadOnlyDictionary<string, string> createdIds)
    {
        var updated = new JsonObject();
        var notUpdated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            var command = call.Command.Updates[index];
            if (outcome is null || !Enum.IsDefined(outcome.Error)
                || !string.Equals(outcome.RequestedId, command.RequestedId, StringComparison.Ordinal)
                || outcome.Error == MailPushSubscriptionMutationError.None
                    != (outcome.SubscriptionId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent push update.");
            if (outcome.Error == MailPushSubscriptionMutationError.None)
            {
                updated[DisplayId(command.RequestedId, call.KnownEntities, createdIds)] =
                    outcome.RevisedExpiresAt is { } revised
                        ? new JsonObject { ["expires"] = GatewayJmapDateCodec.FormatUtc(revised) }
                        : null;
                continue;
            }
            notUpdated[outcome.RequestedId] = outcome.Error == MailPushSubscriptionMutationError.Skipped
                ? (JsonObject)call.UpdateErrors[outcome.RequestedId].DeepClone()
                : ItemError(outcome.Error, outcome.InvalidProperties);
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroyed(Call call,
        IReadOnlyList<MailPushSubscriptionDestroyOutcome> outcomes,
        IReadOnlyDictionary<string, string> createdIds)
    {
        var destroyed = new JsonArray();
        var notDestroyed = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            var command = call.Command.Destroys[index];
            if (outcome is null || !Enum.IsDefined(outcome.Error)
                || !string.Equals(outcome.RequestedId, command.RequestedId, StringComparison.Ordinal)
                || outcome.Error == MailPushSubscriptionMutationError.None
                    != (outcome.SubscriptionId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent push deletion.");
            if (outcome.Error == MailPushSubscriptionMutationError.None)
                destroyed.Add(DisplayId(command.RequestedId, call.KnownEntities, createdIds));
            else
                notDestroyed[outcome.RequestedId] = ItemError(outcome.Error);
        }
        return (destroyed, notDestroyed);
    }

    private static bool TryCreate(JsonObject request, out MailPushSubscriptionCreateValues? values)
    {
        values = null;
        if (request.Any(item => item.Key is not
                ("deviceClientId" or "url" or "keys" or "verificationCode" or "expires" or "types"))
            || !TryRequiredString(request, "deviceClientId", out var deviceId)
            || deviceId.Length is < 1 or > 255
            || !TryRequiredString(request, "url", out var url) || url.Length > 2048
            || request["verificationCode"] is not null
            || !TryKeys(request["keys"], out var keys)
            || !TryDate(request["expires"], out var expires)
            || !TryTypes(request["types"], out var types)) return false;
        values = new(deviceId, url, keys, request.ContainsKey("keys"), expires,
            request.ContainsKey("expires"), types, request.ContainsKey("types"));
        return true;
    }

    private static MailPushSubscriptionPatch? TryPatch(JsonObject patch, out JsonObject? issue)
    {
        issue = null;
        if (!TryPaths(patch, out var paths))
        {
            issue = new JsonObject { ["type"] = "invalidPatch" };
            return null;
        }
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var keyParts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (ref readonly var entry in CollectionsMarshal.AsSpan(paths))
        {
            if (entry.Path.Length == 1) fields[entry.Path[0]] = entry.Value;
            else if (entry.Path.Length == 2 && string.Equals(entry.Path[0], "keys", StringComparison.Ordinal))
                keyParts[entry.Path[1]] = entry.Value;
            else
            {
                issue = new JsonObject { ["type"] = "invalidPatch" };
                return null;
            }
        }
        var invalid = new List<string>();
        if (!TryString(fields.GetValueOrDefault("verificationCode"), out var verification)) invalid.Add("verificationCode");
        if (!TryDate(fields.GetValueOrDefault("expires"), out var expires)) invalid.Add("expires");
        if (!TryTypes(fields.GetValueOrDefault("types"), out var types)) invalid.Add("types");
        if (!TryString(fields.GetValueOrDefault("id"), out var id)) invalid.Add("id");
        if (!TryString(fields.GetValueOrDefault("deviceClientId"), out var deviceId)) invalid.Add("deviceClientId");
        if (!TryString(fields.GetValueOrDefault("url"), out var url)) invalid.Add("url");
        if (!TryKeys(fields.GetValueOrDefault("keys"), out var keys)) invalid.Add("keys");
        var p256dhValid = TryString(keyParts.GetValueOrDefault("p256dh"), out var p256dh);
        var authValid = TryString(keyParts.GetValueOrDefault("auth"), out var auth);
        if (!p256dhValid || !authValid) invalid.Add("keys");
        if (invalid.Count > 0)
        {
            issue = ItemError(MailPushSubscriptionMutationError.InvalidProperties, invalid);
            return null;
        }
        var unknown = fields.Keys.Where(key => key is not
            ("verificationCode" or "expires" or "types" or "id" or "deviceClientId" or "url" or "keys"))
            .Order(StringComparer.Ordinal).ToArray();
        return new(fields.ContainsKey("verificationCode"), verification,
            fields.ContainsKey("expires"), expires,
            fields.ContainsKey("types"), types,
            fields.ContainsKey("id"), id,
            fields.ContainsKey("deviceClientId"), deviceId,
            fields.ContainsKey("url"), url,
            fields.ContainsKey("keys"), keys,
            keyParts.ContainsKey("p256dh"), p256dh,
            keyParts.ContainsKey("auth"), auth,
            unknown, keyParts.Keys.Any(key => key is not ("p256dh" or "auth")),
            keyParts.Any(item => item.Key is not ("p256dh" or "auth") && item.Value is not null));
    }

    private static bool TryMap(JsonObject arguments, string name, bool creation,
        out Dictionary<string, JsonObject> values)
    {
        values = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null) return true;
        if (node is not JsonObject map || map.Any(item => item.Value is not JsonObject
            || !IsReference(item.Key, creation))) return false;
        values = map.ToDictionary(item => item.Key, item => (JsonObject)item.Value!, StringComparer.Ordinal);
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

    private static bool IsReference(string value, bool creation) =>
        GatewayJmapBatchCodec.IsId(value)
        || !creation && value.Length > 1 && value[0] == '#'
            && GatewayJmapBatchCodec.IsId(value[1..]);

    private static MailPushSubscriptionTarget ParseTarget(string value, IReadOnlyDictionary<string, string> known)
    {
        if (value.StartsWith('#'))
        {
            var key = value[1..];
            if (known.TryGetValue(key, out var resolved)) value = resolved;
            else return new(null, key);
        }
        return value.Length == 33 && value[0] == 'P'
            && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
                ? new(id, null) : new(null, null);
    }

    private static bool TryRequiredString(JsonObject value, string key, out string result)
    {
        result = string.Empty;
        return value[key] is JsonValue scalar && scalar.TryGetValue<string>(out result!) && result is not null;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        value = null;
        return node is null || node is JsonValue scalar && scalar.TryGetValue<string>(out value);
    }

    private static bool TryDate(JsonNode? node, out DateTimeOffset? value)
    {
        value = null;
        if (node is null) return true;
        if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)
            || !GatewayJmapDateCodec.TryParseUtc(text, out var parsed)) return false;
        value = new DateTimeOffset(parsed);
        return true;
    }

    private static bool TryTypes(JsonNode? node, out IReadOnlyList<string>? values)
    {
        values = null;
        if (node is null) return true;
        if (node is not JsonArray array) return false;
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue scalar || !scalar.TryGetValue<string>(out var value)
                || value is null) return false;
            result.Add(value);
        }
        values = result.ToArray();
        return true;
    }

    private static bool TryKeys(JsonNode? node, out MailPushSubscriptionKeys? keys)
    {
        keys = null;
        if (node is null) return true;
        if (node is not JsonObject value || value.Count != 2
            || !TryRequiredString(value, "p256dh", out var p256dh)
            || !TryRequiredString(value, "auth", out var auth)) return false;
        keys = new(p256dh, auth);
        return true;
    }

    private static bool TryPaths(JsonObject patch, out List<(string[] Path, JsonNode? Value)> paths)
    {
        paths = new(patch.Count);
        foreach (var item in patch)
        {
            if (!TryDecodePath(item.Key, out var path)
                || paths.Any(existing => IsPrefix(existing.Path, path) || IsPrefix(path, existing.Path)))
                return false;
            paths.Add((path, item.Value));
        }
        return true;
    }

    private static bool IsPrefix(string[] left, string[] right)
    {
        if (left.Length > right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool TryDecodePath(string value, out string[] path)
    {
        path = [];
        if (value.Length == 0 || value[0] == '/' || value[^1] == '/') return false;
        var parts = value.Split('/');
        var decoded = new string[parts.Length];
        for (var partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            var builder = new StringBuilder(parts[partIndex].Length);
            for (var index = 0; index < parts[partIndex].Length; index++)
            {
                if (parts[partIndex][index] != '~')
                {
                    builder.Append(parts[partIndex][index]);
                    continue;
                }
                if (++index >= parts[partIndex].Length || parts[partIndex][index] is not ('0' or '1'))
                    return false;
                builder.Append(parts[partIndex][index] == '0' ? '~' : '/');
            }
            decoded[partIndex] = builder.ToString();
            if (decoded[partIndex].Length == 0) return false;
        }
        path = decoded;
        return true;
    }

    private static JsonObject? RenderKeys(MailPushSubscriptionKeys? keys) => keys is null
        ? null : new JsonObject { ["p256dh"] = keys.P256dh, ["auth"] = keys.Auth };

    private static JsonArray? RenderTypes(IReadOnlyList<string>? types) => types is null
        ? null : new JsonArray(types.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static string FormatId(Guid id) => $"P{id:N}";

    private static string DisplayId(string requested, IReadOnlyDictionary<string, string> known,
        IReadOnlyDictionary<string, string> created) => requested.StartsWith('#')
            ? created.GetValueOrDefault(requested[1..], known.GetValueOrDefault(requested[1..], requested))
            : requested;

    private static JsonObject ItemError(MailPushSubscriptionMutationError error,
        IReadOnlyList<string>? properties = null)
    {
        var type = error switch
        {
            MailPushSubscriptionMutationError.InvalidProperties => "invalidProperties",
            MailPushSubscriptionMutationError.NotFound => "notFound",
            MailPushSubscriptionMutationError.OverQuota => "overQuota",
            MailPushSubscriptionMutationError.RateLimit => "rateLimit",
            _ => throw new InvalidOperationException("The Application returned an unknown push mutation error."),
        };
        var result = new JsonObject { ["type"] = type };
        if (properties is { Count: > 0 } && string.Equals(type, "invalidProperties", StringComparison.Ordinal))
            result["properties"] = new JsonArray(properties.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return result;
    }
}
