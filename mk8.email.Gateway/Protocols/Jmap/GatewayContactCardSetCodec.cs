using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayContactCardSetCodec
{
    internal sealed record Call(MailContactMutationCommand Command, string AccountId,
        IReadOnlyDictionary<string, string> KnownEntities,
        IReadOnlyDictionary<string, JsonObject> CreateRequests);

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
        var parseable = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out _);
        var accountGuid = parseable ? Guid.ParseExact(accountId.AsSpan(1), "N") : Guid.Empty;
        var eligible = parseable && string.Equals(accountId, $"A{accountGuid:N}", StringComparison.Ordinal);
        var bookAliases = knownEntities.Where(item => TryBookId(item.Value, out _))
            .ToDictionary(item => "#" + item.Key, item => ParseBookIdValue(item.Value), StringComparer.Ordinal);
        var requests = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var createCommands = creates.Select(item =>
        {
            var card = (JsonObject)item.Value.DeepClone();
            var hasId = card.Remove("id");
            var bookId = ParseBookId(card["addressBookIds"], knownEntities);
            card.Remove("addressBookIds");
            requests[item.Key] = card;
            return new MailContactCreate(item.Key, hasId, bookId, ApplicationValueCodec.Encode(card));
        }).ToArray();
        var updateCommands = updates.Select(item =>
        {
            var valid = TryPatch(item.Value, out var patch);
            return new MailContactUpdate(item.Key, ParseTarget(item.Key, knownEntities), valid ? patch : null);
        }).ToArray();
        var destroyCommands = destroys.Select(item => new MailContactDestroy(item,
            ParseTarget(item, knownEntities))).ToArray();
        call = new(new(accountGuid, parseable, eligible, ifInState, bookAliases,
            createCommands, updateCommands, destroyCommands), accountId, knownEntities, requests);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call,
        MailContactMutationResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Created is null || result.Updated is null || result.Destroyed is null)
            throw new InvalidOperationException("The Application returned an invalid contact mutation.");
        if (result.Status == MailContactMutationStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailContactMutationStatus.AccountNotSupported)
            return Error("accountNotSupportedByMethod");
        if (result.Status == MailContactMutationStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status != MailContactMutationStatus.Ok || result.OldState is null || result.NewState is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete contact mutation outcomes.");
        var (created, notCreated, createdIds) = RenderCreated(call, result.Created);
        var (updated, notUpdated) = RenderUpdated(call, result.Updated, createdIds);
        var (destroyed, notDestroyed) = RenderDestroyed(call, result.Destroyed, createdIds);
        return (MailOperationKind.MutateContacts, new JsonObject
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
        RenderCreated(Call call, IReadOnlyList<MailContactCreateOutcome> outcomes)
    {
        var created = new JsonObject();
        var notCreated = new JsonObject();
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            if (outcome is null || !Enum.IsDefined(outcome.Error)
                || !string.Equals(outcome.CreationId, call.Command.Creates[index].CreationId, StringComparison.Ordinal)
                || outcome.Error == MailContactMutationError.None
                    != (outcome.CardId is { } id && id != Guid.Empty && outcome.StoredCard is not null))
                throw new InvalidOperationException("The Application returned inconsistent contact creation.");
            if (outcome.Error != MailContactMutationError.None)
            {
                notCreated[outcome.CreationId] = ItemError(outcome.Error, outcome.InvalidProperties);
                continue;
            }
            var wireId = FormatId(outcome.CardId!.Value);
            createdIds[outcome.CreationId] = wireId;
            var stored = DecodeCard(outcome.StoredCard!);
            var response = ServerChanges(call.CreateRequests[outcome.CreationId], stored);
            response["id"] = wireId;
            created[outcome.CreationId] = response;
        }
        return (created, notCreated, createdIds);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdated(Call call,
        IReadOnlyList<MailContactUpdateOutcome> outcomes,
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
                || outcome.Error == MailContactMutationError.None
                    != (outcome.CardId is { } id && id != Guid.Empty
                        && outcome.RequestedCard is not null && outcome.StoredCard is not null))
                throw new InvalidOperationException("The Application returned inconsistent contact update.");
            if (outcome.Error == MailContactMutationError.None)
            {
                var changes = ServerChanges(DecodeCard(outcome.RequestedCard!),
                    DecodeCard(outcome.StoredCard!));
                updated[DisplayId(command.RequestedId, call.KnownEntities, createdIds)] =
                    changes.Count == 0 ? null : changes;
            }
            else notUpdated[outcome.RequestedId] = ItemError(outcome.Error, outcome.InvalidProperties);
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroyed(Call call,
        IReadOnlyList<MailContactDestroyOutcome> outcomes,
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
                || outcome.Error == MailContactMutationError.None
                    != (outcome.CardId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent contact deletion.");
            if (outcome.Error == MailContactMutationError.None)
                destroyed.Add(DisplayId(command.RequestedId, call.KnownEntities, createdIds));
            else notDestroyed[outcome.RequestedId] = ItemError(outcome.Error, null);
        }
        return (destroyed, notDestroyed);
    }

    private static bool TryPatch(JsonObject patch, out MailContactPatchEntry[] result)
    {
        result = [];
        var entries = new List<MailContactPatchEntry>(patch.Count);
        foreach (var item in patch)
        {
            if (!TryDecodePath(item.Key, out var path)
                || entries.Any(existing => IsPrefix(existing.Path, path) || IsPrefix(path, existing.Path)))
                return false;
            var value = item.Value?.DeepClone();
            entries.Add(new(path, ApplicationValueCodec.Encode(value)));
        }
        result = entries.ToArray();
        return true;
    }

    private static string ResolveKnown(string value, IReadOnlyDictionary<string, string> knownEntities) =>
        value.Length > 1 && value[0] == '#' && knownEntities.TryGetValue(value[1..], out var resolved)
            ? resolved : value;

    private static Guid? ParseBookId(JsonNode? node, IReadOnlyDictionary<string, string> knownEntities)
    {
        if (node is not JsonObject ids || ids.Count != 1) return null;
        var item = ids.First();
        if (item.Value is not JsonValue flag || !flag.TryGetValue<bool>(out var included) || !included)
            return null;
        var value = ResolveKnown(item.Key, knownEntities);
        return TryBookId(value, out var id) ? id : null;
    }

    private static bool TryBookId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == 'D'
            && Guid.TryParseExact(value.AsSpan(1), "N", out id)
            && string.Equals(value, $"D{id:N}", StringComparison.Ordinal);
    }

    private static Guid ParseBookIdValue(string value) => TryBookId(value, out var id)
        ? id : throw new InvalidOperationException("Invalid address-book alias.");

    private static MailContactTarget ParseTarget(string value, IReadOnlyDictionary<string, string> knownEntities)
    {
        if (value.StartsWith('#'))
        {
            var key = value[1..];
            if (knownEntities.TryGetValue(key, out var resolved)) value = resolved;
            else return new(null, key);
        }
        return value.Length == 33 && value[0] == 'C'
            && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
                ? new(id, null) : new(null, null);
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

    private static bool IsReference(string value, bool creation) => GatewayJmapBatchCodec.IsId(value)
        || !creation && value.Length > 1 && value[0] == '#' && GatewayJmapBatchCodec.IsId(value[1..]);

    private static bool TryOptionalString(JsonObject value, string key, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(key, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue<string>(out result);
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

    private static bool IsPrefix(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count > right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static JsonObject DecodeCard(ApplicationValue value)
    {
        var card = ApplicationValueCodec.Decode(value) as JsonObject
            ?? throw new InvalidOperationException("The Application returned an invalid JSContact document.");
        if (!string.Equals(card["@type"]?.GetValue<string>(), "Card", StringComparison.Ordinal)
            || !string.Equals(card["version"]?.GetValue<string>(), "1.0", StringComparison.Ordinal)
            || card["uid"] is not JsonValue uid || !uid.TryGetValue<string>(out _)
            || card.ContainsKey("id") || card.ContainsKey("addressBookIds"))
            throw new InvalidOperationException("The Application returned an inconsistent JSContact document.");
        return card;
    }

    private static JsonObject ServerChanges(JsonObject requested, JsonObject stored)
    {
        var result = new JsonObject();
        foreach (var property in stored)
        {
            if (!requested.TryGetPropertyValue(property.Key, out var value)
                || !JsonNode.DeepEquals(value, property.Value))
                result[property.Key] = property.Value?.DeepClone();
        }
        foreach (var property in requested)
            if (!stored.ContainsKey(property.Key)) result[property.Key] = null;
        return result;
    }

    private static JsonObject ItemError(MailContactMutationError error, IReadOnlyList<string>? properties)
    {
        var type = error switch
        {
            MailContactMutationError.InvalidProperties => "invalidProperties",
            MailContactMutationError.InvalidPatch => "invalidPatch",
            MailContactMutationError.NotFound => "notFound",
            MailContactMutationError.OverQuota => "overQuota",
            MailContactMutationError.WillDestroy => "willDestroy",
            _ => throw new InvalidOperationException("The Application returned an unknown contact error."),
        };
        var result = new JsonObject { ["type"] = type };
        if (properties is { Count: > 0 } && string.Equals(type, "invalidProperties", StringComparison.Ordinal))
            result["properties"] = new JsonArray(properties.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return result;
    }

    private static string DisplayId(string requested, IReadOnlyDictionary<string, string> known,
        IReadOnlyDictionary<string, string> created) => requested.StartsWith('#')
            ? created.GetValueOrDefault(requested[1..], known.GetValueOrDefault(requested[1..], requested))
            : requested;

    private static string FormatId(Guid id) => $"C{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
