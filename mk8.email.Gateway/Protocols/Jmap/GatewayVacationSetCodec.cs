using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayVacationSetCodec
{
    internal sealed record UpdateSelection(string Key, int? MutationIndex, string? Error, IReadOnlyList<string>? Properties);

    internal sealed record Call(
        MailVacationSetCommand Command,
        string AccountId,
        IReadOnlyList<string> CreateKeys,
        IReadOnlyList<UpdateSelection> Updates,
        IReadOnlyList<string> DestroyIds,
        IReadOnlyDictionary<string, string> KnownEntities);

    public static bool TryParse(
        JsonObject arguments,
        IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects,
        out Call? call,
        out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "ifInState" or "create" or "update" or "destroy"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
            || !TryObjectMap(arguments, "create", out var creates)
            || !TryObjectMap(arguments, "update", out var updates)
            || !TryDestroy(arguments, out var destroys)
            || creates.Any(item => !GatewayJmapBatchCodec.IsId(item.Key))
            || updates.Any(item => !IsIdReference(item.Key))
            || destroys.Any(id => !IsIdReference(id)))
        {
            failure = "invalidArguments";
            return false;
        }
        if (creates.Count + updates.Count + destroys.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }

        var mutations = new List<MailVacationUpdate>();
        var selections = new List<UpdateSelection>();
        foreach (var item in updates)
        {
            if (!string.Equals(Resolve(item.Key, knownEntities), "singleton", StringComparison.Ordinal))
            {
                selections.Add(new(item.Key, null, "notFound", null));
                continue;
            }
            if (!TryParsePatch(item.Value, out var mutation, out var error, out var properties))
            {
                selections.Add(new(item.Key, null, error, properties));
                continue;
            }
            selections.Add(new(item.Key, mutations.Count, null, null));
            mutations.Add(mutation!);
        }
        var accountGuid = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var parsed)
                ? parsed
                : Guid.Empty;
        call = new(new(accountGuid, ifInState, mutations), accountId,
            creates.Select(item => item.Key).ToArray(), selections, destroys, knownEntities);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailVacationSetResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Updates is null)
            throw new InvalidOperationException("The Application returned an invalid vacation mutation result.");
        if (result.Status == MailVacationSetStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailVacationSetStatus.StateMismatch)
            return Error("stateMismatch");
        if (result.OldState is null || result.NewState is null
            || result.Updates.Count != call.Command.Updates.Count)
            throw new InvalidOperationException("The Application returned an incomplete vacation mutation result.");

        var notCreated = new JsonObject();
        foreach (var key in call.CreateKeys)
            notCreated[key] = SetError("singleton");
        var updated = new JsonObject();
        var notUpdated = new JsonObject();
        foreach (var selection in call.Updates)
        {
            if (selection.Error is not null)
            {
                notUpdated[selection.Key] = SetError(selection.Error, selection.Properties);
                continue;
            }
            var outcome = result.Updates[selection.MutationIndex!.Value];
            if (outcome is null || outcome.InvalidProperties is null
                || outcome.Updated == (outcome.InvalidProperties.Count > 0)
                || outcome.InvalidProperties.Any(name => name is not (
                    "isEnabled" or "fromDate" or "toDate" or "subject" or "textBody" or "htmlBody")))
                throw new InvalidOperationException("The Application returned an invalid vacation update outcome.");
            if (outcome.Updated)
                updated["singleton"] = null;
            else
                notUpdated[selection.Key] = SetError("invalidProperties", outcome.InvalidProperties);
        }
        var notDestroyed = new JsonObject();
        foreach (var id in call.DestroyIds.Distinct(StringComparer.Ordinal))
            notDestroyed[id] = SetError(string.Equals(Resolve(id, call.KnownEntities), "singleton", StringComparison.Ordinal)
                ? "singleton" : "notFound");
        return (MailOperationKind.MutateVacationSettings, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldState"] = result.OldState,
            ["newState"] = result.NewState,
            ["created"] = null,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = null,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static bool TryParsePatch(JsonObject patch, out MailVacationUpdate? update,
        out string? error, out IReadOnlyList<string>? properties)
    {
        update = null;
        error = null;
        properties = null;
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in patch)
        {
            if (!TryDecodeSingleToken(item.Key, out var name))
            {
                error = "invalidPatch";
                return false;
            }
            fields[name] = item.Value;
        }
        var immutable = fields.Where(item => item.Key is not (
                "isEnabled" or "fromDate" or "toDate" or "subject" or "textBody" or "htmlBody")
                && (!string.Equals(item.Key, "id", StringComparison.Ordinal) || item.Value is not JsonValue idNode
                    || !idNode.TryGetValue<string>(out var id) || !string.Equals(id, "singleton", StringComparison.Ordinal)))
            .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        if (immutable.Length > 0)
        {
            error = "invalidProperties";
            properties = immutable;
            return false;
        }

        var invalid = new List<string>();
        var setEnabled = fields.TryGetValue("isEnabled", out var enabledNode);
        var enabled = false;
        if (setEnabled && (enabledNode is not JsonValue boolNode || !boolNode.TryGetValue<bool>(out enabled)))
            invalid.Add("isEnabled");
        var setFrom = fields.TryGetValue("fromDate", out var fromNode);
        if (!TryDate(fromNode, out var fromDate)) invalid.Add("fromDate");
        var setTo = fields.TryGetValue("toDate", out var toNode);
        if (!TryDate(toNode, out var toDate)) invalid.Add("toDate");
        var setSubject = fields.TryGetValue("subject", out var subjectNode);
        if (!TryString(subjectNode, out var subject) || subject is { Length: > 998 }) invalid.Add("subject");
        var setText = fields.TryGetValue("textBody", out var textNode);
        if (!TryString(textNode, out var text)) invalid.Add("textBody");
        var setHtml = fields.TryGetValue("htmlBody", out var htmlNode);
        if (!TryString(htmlNode, out var html)) invalid.Add("htmlBody");
        if (invalid.Count > 0)
        {
            error = "invalidProperties";
            properties = invalid.ToArray();
            return false;
        }
        update = new(setEnabled, enabled, setFrom, fromDate, setTo, toDate,
            setSubject, subject, setText, text, setHtml, html);
        return true;
    }

    private static bool TryDecodeSingleToken(string path, out string name)
    {
        name = string.Empty;
        if (path.Length == 0 || path.Contains('/', StringComparison.Ordinal))
            return false;
        var characters = new System.Text.StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] != '~')
            {
                characters.Append(path[index]);
                continue;
            }
            if (++index >= path.Length || path[index] is not ('0' or '1'))
                return false;
            characters.Append(path[index] == '0' ? '~' : '/');
        }
        name = characters.ToString();
        return name.Length > 0;
    }

    private static bool TryDate(JsonNode? node, out DateTime? date)
    {
        date = null;
        if (node is null) return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !GatewayJmapDateCodec.TryParseUtc(text, out var parsed)) return false;
        date = parsed;
        return true;
    }

    private static bool TryString(JsonNode? node, out string? text)
    {
        text = null;
        return node is null || node is JsonValue value && value.TryGetValue(out text);
    }

    private static bool TryOptionalString(JsonObject arguments, string key, out string? value)
    {
        value = null;
        return !arguments.TryGetPropertyValue(key, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue(out value);
    }

    private static bool TryObjectMap(JsonObject arguments, string key, out Dictionary<string, JsonObject> values)
    {
        values = new(StringComparer.Ordinal);
        if (!arguments.TryGetPropertyValue(key, out var node) || node is null) return true;
        if (node is not JsonObject map) return false;
        foreach (var item in map)
        {
            if (item.Value is not JsonObject value) return false;
            values.Add(item.Key, value);
        }
        return true;
    }

    private static bool TryDestroy(JsonObject arguments, out List<string> values)
    {
        values = [];
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null) return true;
        if (node is not JsonArray array) return false;
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || id is null)
                return false;
            values.Add(id);
        }
        return true;
    }

    private static bool IsIdReference(string value) => GatewayJmapBatchCodec.IsId(value)
        || value.Length > 1 && value[0] == '#' && GatewayJmapBatchCodec.IsId(value[1..]);

    private static string? Resolve(string value, IReadOnlyDictionary<string, string> knownEntities) =>
        value.Length > 0 && value[0] == '#'
            ? knownEntities.GetValueOrDefault(value[1..])
            : value;

    private static JsonObject SetError(string type, IReadOnlyList<string>? properties = null)
    {
        var error = new JsonObject { ["type"] = type };
        if (properties is not null)
            error["properties"] = new JsonArray(properties.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
        return error;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
