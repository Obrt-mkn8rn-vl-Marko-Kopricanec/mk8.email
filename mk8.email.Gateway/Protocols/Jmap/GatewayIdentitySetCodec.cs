using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayIdentitySetCodec
{
    internal sealed record Call(MailIdentityMutationCommand Command, string AccountId,
        IReadOnlyDictionary<string, string> KnownEntities,
        IReadOnlyDictionary<string, JsonObject> CreateRequests,
        IReadOnlyDictionary<string, JsonObject> UpdateErrors);

    public static bool TryParse(JsonObject arguments, IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ifInState" or "create" or "update" or "destroy"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
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
        var createRequests = creates.ToDictionary(item => item.Key,
            item => item.Value, StringComparer.Ordinal);
        var updateErrors = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var createCommands = creates.Select(item => new MailIdentityCreate(item.Key,
            TryParseValues(item.Value, out var values) ? values : null)).ToArray();
        var updateCommands = updates.Select(item =>
        {
            var patch = TryParsePatch(item.Value, out var issue);
            if (issue is not null) updateErrors[item.Key] = issue;
            return new MailIdentityUpdate(item.Key, ParseTarget(item.Key, knownEntities), patch);
        }).ToArray();
        var destroyCommands = destroys.Distinct(StringComparer.Ordinal).Select(id =>
            new MailIdentityDestroy(id, ParseTarget(id, knownEntities))).ToArray();
        var account = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var parsed)
                ? parsed : Guid.Empty;
        call = new(new(account, ifInState, createCommands, updateCommands, destroyCommands),
            accountId, knownEntities, createRequests, updateErrors);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call,
        MailIdentityMutationResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Created is null || result.Updated is null || result.Destroyed is null)
            throw new InvalidOperationException("The Application returned an invalid identity mutation.");
        if (result.Status == MailIdentityMutationStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailIdentityMutationStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status != MailIdentityMutationStatus.Ok || result.OldState is null || result.NewState is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete identity mutation outcomes.");
        var (created, notCreated, createdIds) = RenderCreated(call, result.Created);
        var (updated, notUpdated) = RenderUpdated(call, result.Updated, createdIds);
        var (destroyed, notDestroyed) = RenderDestroyed(call, result.Destroyed, createdIds);
        return (MailOperationKind.MutateSenderIdentities, new JsonObject
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

    private static (JsonObject Created, JsonObject NotCreated, Dictionary<string, string> CreatedIds)
        RenderCreated(Call call, IReadOnlyList<MailIdentityCreateOutcome> outcomes)
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
                || outcome.Error == MailIdentityMutationError.Skipped && command.Values is not null
                || outcome.Error == MailIdentityMutationError.None != (outcome.IdentityId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent identity creation.");
            if (outcome.Error != MailIdentityMutationError.None)
            {
                notCreated[outcome.CreationId] = ItemError(outcome.Error == MailIdentityMutationError.Skipped
                    ? MailIdentityMutationError.InvalidProperties : outcome.Error);
                continue;
            }
            var wireId = FormatId(outcome.IdentityId!.Value);
            createdIds[outcome.CreationId] = wireId;
            var request = call.CreateRequests[outcome.CreationId];
            var response = new JsonObject { ["id"] = wireId, ["mayDelete"] = true };
            if (!request.ContainsKey("name")) response["name"] = string.Empty;
            if (!request.ContainsKey("replyTo")) response["replyTo"] = null;
            if (!request.ContainsKey("bcc")) response["bcc"] = null;
            if (!request.ContainsKey("textSignature")) response["textSignature"] = string.Empty;
            if (!request.ContainsKey("htmlSignature")) response["htmlSignature"] = string.Empty;
            created[outcome.CreationId] = response;
        }
        return (created, notCreated, createdIds);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdated(
        Call call, IReadOnlyList<MailIdentityUpdateOutcome> outcomes,
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
                || outcome.Error == MailIdentityMutationError.Skipped && command.Patch is not null
                || outcome.Error == MailIdentityMutationError.None != (outcome.IdentityId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent identity update.");
            if (outcome.Error == MailIdentityMutationError.None)
            {
                updated[DisplayId(command.RequestedId, call.KnownEntities, createdIds)] = null;
                continue;
            }
            notUpdated[outcome.RequestedId] = outcome.Error == MailIdentityMutationError.Skipped
                ? (JsonObject)call.UpdateErrors[outcome.RequestedId].DeepClone()
                : ItemError(outcome.Error, outcome.InvalidFields?.Select(ImmutableField).ToArray());
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroyed(
        Call call, IReadOnlyList<MailIdentityDestroyOutcome> outcomes,
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
                || outcome.Error == MailIdentityMutationError.None != (outcome.IdentityId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent identity deletion.");
            if (outcome.Error == MailIdentityMutationError.None)
                destroyed.Add(DisplayId(command.RequestedId, call.KnownEntities, createdIds));
            else
                notDestroyed[outcome.RequestedId] = ItemError(outcome.Error);
        }
        return (destroyed, notDestroyed);
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

    private static bool TryOptionalString(JsonObject value, string key, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(key, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue<string>(out result);
    }

    private static MailIdentityTarget ParseTarget(string value, IReadOnlyDictionary<string, string> known)
    {
        if (value.StartsWith('#'))
        {
            var key = value[1..];
            if (known.TryGetValue(key, out var resolved)) value = resolved;
            else return new(null, key);
        }
        return value.Length == 33 && value[0] == 'I'
            && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
                ? new(id, null) : new(null, null);
    }

    private static bool TryParseValues(JsonObject value, out MailIdentityValues? result)
    {
        result = null;
        if (value.Any(item => item.Key is not ("name" or "email" or "replyTo" or "bcc"
                or "textSignature" or "htmlSignature"))
            || !TryDefaultString(value, "name", out var name)
            || !TryDefaultString(value, "textSignature", out var textSignature)
            || !TryDefaultString(value, "htmlSignature", out var htmlSignature)
            || !TryOptionalString(value, "email", out var email)
            || !TryAddresses(value["replyTo"], out var replyTo)
            || !TryAddresses(value["bcc"], out var bcc)) return false;
        result = new(name, email, replyTo, bcc, textSignature, htmlSignature);
        return true;
    }

    private static bool TryDefaultString(JsonObject value, string key, out string result)
    {
        result = string.Empty;
        if (!value.TryGetPropertyValue(key, out var node)) return true;
        if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out var parsed)
            || parsed is null) return false;
        result = parsed;
        return true;
    }

    private static bool TryAddresses(JsonNode? node, out MailIdentityAddressListSnapshot? addresses)
    {
        addresses = null;
        if (node is null) return true;
        if (node is not JsonArray array) return false;
        var parsed = new List<MailIdentityAddressSnapshot>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject address
                || address.Any(field => field.Key is not ("name" or "email"))
                || address["email"] is not JsonValue emailNode
                || !emailNode.TryGetValue<string>(out var email) || email is null
                || !TryOptionalString(address, "name", out var name)) return false;
            parsed.Add(new(name, email));
        }
        addresses = new(parsed);
        return true;
    }

    private static MailIdentityPatch? TryParsePatch(JsonObject value, out JsonObject? issue)
    {
        issue = null;
        if (value.Any(item => item.Key.Length == 0
            || item.Key.Contains('/', StringComparison.Ordinal) || HasInvalidEscape(item.Key)))
        {
            issue = ItemError(MailIdentityMutationError.Skipped, ["invalidPatch"]);
            return null;
        }
        var invalid = value.Where(item => item.Value is not null && item.Key is not
            ("name" or "replyTo" or "bcc" or "textSignature" or "htmlSignature" or "id" or "email" or "mayDelete"))
            .Select(item => item.Key).ToArray();
        if (invalid.Length > 0)
        {
            issue = ItemError(MailIdentityMutationError.InvalidProperties, invalid);
            return null;
        }
        if (!TryPatchString(value, "name", out var name)
            || !TryPatchString(value, "textSignature", out var text)
            || !TryPatchString(value, "htmlSignature", out var html)
            || !TryAddresses(value["replyTo"], out var replyTo)
            || !TryAddresses(value["bcc"], out var bcc)
            || !TryPatchString(value, "id", out var id)
            || !TryPatchString(value, "email", out var email)
            || value.TryGetPropertyValue("mayDelete", out var mayDeleteNode)
                && mayDeleteNode is not null
                && (mayDeleteNode is not JsonValue boolNode || !boolNode.TryGetValue<bool>(out _)))
        {
            issue = new JsonObject { ["type"] = "invalidProperties" };
            return null;
        }
        var mayDelete = value["mayDelete"] is JsonValue flag && flag.TryGetValue<bool>(out var parsed)
            ? parsed : (bool?)null;
        return new(value.ContainsKey("name"), name,
            value.ContainsKey("replyTo"), replyTo,
            value.ContainsKey("bcc"), bcc,
            value.ContainsKey("textSignature"), text,
            value.ContainsKey("htmlSignature"), html,
            value.ContainsKey("id"), id,
            value.ContainsKey("email"), email,
            value.ContainsKey("mayDelete"), mayDelete);
    }

    private static bool TryPatchString(JsonObject value, string key, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(key, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue<string>(out result);
    }

    private static bool HasInvalidEscape(string key)
    {
        for (var index = 0; index < key.Length; index++)
        {
            if (key[index] != '~') continue;
            if (++index >= key.Length || key[index] is not ('0' or '1')) return true;
        }
        return false;
    }

    private static string DisplayId(string requested,
        IReadOnlyDictionary<string, string> known, IReadOnlyDictionary<string, string> created) =>
        requested.StartsWith('#')
            ? created.GetValueOrDefault(requested[1..], known.GetValueOrDefault(requested[1..], requested))
            : requested;

    private static string FormatId(Guid id) => $"I{id:N}";

    private static string ImmutableField(MailIdentityImmutableField field) => field switch
    {
        MailIdentityImmutableField.Id => "id",
        MailIdentityImmutableField.Email => "email",
        MailIdentityImmutableField.MayDelete => "mayDelete",
        _ => throw new InvalidOperationException("The Application returned an unknown immutable field."),
    };

    private static JsonObject ItemError(MailIdentityMutationError error, IReadOnlyList<string>? fields = null)
    {
        var type = error switch
        {
            MailIdentityMutationError.Skipped => "invalidPatch",
            MailIdentityMutationError.InvalidProperties => "invalidProperties",
            MailIdentityMutationError.ForbiddenFrom => "forbiddenFrom",
            MailIdentityMutationError.NotFound => "notFound",
            MailIdentityMutationError.WillDestroy => "willDestroy",
            MailIdentityMutationError.Forbidden => "forbidden",
            _ => throw new InvalidOperationException("The Application returned an invalid identity error."),
        };
        var result = new JsonObject { ["type"] = type };
        if (fields is { Count: > 0 } && string.Equals(type, "invalidProperties", StringComparison.Ordinal))
            result["properties"] = new JsonArray(fields.Select(field => (JsonNode?)JsonValue.Create(field)).ToArray());
        return result;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
