using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayAddressBookSetCodec
{
    internal sealed record Call(
        MailAddressBookMutationCommand Command,
        string AccountId,
        IReadOnlyDictionary<string, string> KnownEntities,
        IReadOnlyDictionary<string, JsonObject> CreateRequests,
        IReadOnlyDictionary<string, JsonObject> CreateErrors,
        IReadOnlyDictionary<string, JsonObject> UpdateErrors);

    private sealed record PatchEntry(string[] Path, JsonNode? Value);

    public static bool TryParse(JsonObject arguments, IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ifInState" or "create" or "update"
                or "destroy" or "onDestroyRemoveContents" or "onSuccessSetIsDefault"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
            || !TryOptionalBoolean(arguments, "onDestroyRemoveContents", out var removeContents)
            || !TryOptionalString(arguments, "onSuccessSetIsDefault", out var requestedDefault)
            || requestedDefault is not null && !IsReference(requestedDefault, false)
            || !TryMap(arguments, "create", true, out var creates)
            || !TryMap(arguments, "update", false, out var updates)
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
        var createErrors = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var updateErrors = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var createCommands = creates.Select(item =>
        {
            var values = TryParseCreate(item.Value, out var issue);
            if (issue is not null) createErrors[item.Key] = issue;
            return new MailAddressBookCreate(item.Key,
                item.Value.TryGetPropertyValue("shareWith", out var shares) && shares is not null, values);
        }).ToArray();
        var updateCommands = updates.Select(item =>
        {
            var patch = TryParsePatch(item.Value, out var issue);
            if (issue is not null) updateErrors[item.Key] = issue;
            return new MailAddressBookUpdate(item.Key, ParseTarget(item.Key, knownEntities), patch);
        }).ToArray();
        var destroyCommands = destroys.Distinct(StringComparer.Ordinal).Select(id =>
            new MailAddressBookDestroy(id, ParseTarget(id, knownEntities))).ToArray();
        var account = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var parsed) ? parsed : Guid.Empty;
        var eligible = account != Guid.Empty
            && string.Equals(accountId, FormatId('A', account), StringComparison.Ordinal);
        call = new(new(account, eligible, ifInState, removeContents,
                requestedDefault is null ? null : ParseTarget(requestedDefault, knownEntities),
                createCommands, updateCommands, destroyCommands),
            accountId, knownEntities, creates, createErrors, updateErrors);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call,
        MailAddressBookMutationResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Created is null || result.Updated is null || result.Destroyed is null
            || result.DefaultChanges is null)
            throw new InvalidOperationException("The Application returned an invalid address-book mutation.");
        if (result.Status == MailAddressBookMutationStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailAddressBookMutationStatus.AccountNotSupported)
            return Error("accountNotSupportedByMethod");
        if (result.Status == MailAddressBookMutationStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status != MailAddressBookMutationStatus.Ok || result.OldState is null || result.NewState is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete address-book outcomes.");
        var (created, notCreated, createdIds) = RenderCreated(call, result.Created);
        var (updated, notUpdated) = RenderUpdated(call, result.Updated, createdIds);
        var (destroyed, notDestroyed) = RenderDestroyed(call, result.Destroyed, createdIds);
        ApplyDefaultChanges(result.DefaultChanges, createdIds, updated);
        return (MailOperationKind.MutateAddressBooks, new JsonObject
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
        RenderCreated(Call call, IReadOnlyList<MailAddressBookCreateOutcome> outcomes)
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
                || outcome.Error == MailAddressBookMutationError.None != (outcome.Book is not null)
                || outcome.Error == MailAddressBookMutationError.Skipped && command.Values is not null)
                throw new InvalidOperationException("The Application returned inconsistent address-book creation.");
            if (outcome.Error != MailAddressBookMutationError.None)
            {
                notCreated[outcome.CreationId] = outcome.Error == MailAddressBookMutationError.Skipped
                    ? (JsonObject)call.CreateErrors[outcome.CreationId].DeepClone()
                    : ItemError(outcome.Error, outcome.InvalidFields);
                continue;
            }
            var book = outcome.Book!;
            ValidateBook(book);
            var wireId = FormatId('D', book.Id);
            createdIds[outcome.CreationId] = wireId;
            var request = call.CreateRequests[outcome.CreationId];
            var response = new JsonObject
            {
                ["id"] = wireId,
                ["isDefault"] = book.IsDefault,
                ["shareWith"] = null,
                ["myRights"] = Rights(book),
            };
            if (!request.ContainsKey("description")) response["description"] = null;
            if (!request.ContainsKey("sortOrder")) response["sortOrder"] = 0;
            if (!request.ContainsKey("isSubscribed")) response["isSubscribed"] = true;
            if (!string.Equals(request["name"]?.GetValue<string>(), book.Name, StringComparison.Ordinal))
                response["name"] = book.Name;
            created[outcome.CreationId] = response;
        }
        return (created, notCreated, createdIds);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdated(
        Call call, IReadOnlyList<MailAddressBookUpdateOutcome> outcomes,
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
                || outcome.Error == MailAddressBookMutationError.None != (outcome.Book is not null)
                || outcome.Error == MailAddressBookMutationError.Skipped && command.Patch is not null)
                throw new InvalidOperationException("The Application returned inconsistent address-book update.");
            if (outcome.Error != MailAddressBookMutationError.None)
            {
                notUpdated[outcome.RequestedId] = outcome.Error == MailAddressBookMutationError.Skipped
                    ? (JsonObject)call.UpdateErrors[outcome.RequestedId].DeepClone()
                    : ItemError(outcome.Error, outcome.InvalidFields);
                continue;
            }
            ValidateBook(outcome.Book!);
            var response = new JsonObject();
            if (command.Patch!.SetName && !string.Equals(command.Patch.Name, outcome.Book!.Name,
                    StringComparison.Ordinal)) response["name"] = outcome.Book.Name;
            updated[DisplayId(command.RequestedId, call.KnownEntities, createdIds)] =
                response.Count == 0 ? null : response;
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroyed(
        Call call, IReadOnlyList<MailAddressBookDestroyOutcome> outcomes,
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
                || outcome.Error == MailAddressBookMutationError.None
                    != (outcome.BookId is { } id && id != Guid.Empty))
                throw new InvalidOperationException("The Application returned inconsistent address-book deletion.");
            if (outcome.Error == MailAddressBookMutationError.None)
                destroyed.Add(DisplayId(command.RequestedId, call.KnownEntities, createdIds));
            else
                notDestroyed[outcome.RequestedId] = ItemError(outcome.Error);
        }
        return (destroyed, notDestroyed);
    }

    private static void ApplyDefaultChanges(IReadOnlyList<MailAddressBookSnapshot> changes,
        IReadOnlyDictionary<string, string> createdIds, JsonObject updated)
    {
        var newIds = createdIds.Values.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<Guid>();
        foreach (var book in changes)
        {
            ValidateBook(book);
            if (!seen.Add(book.Id))
                throw new InvalidOperationException("The Application returned duplicate default changes.");
            var id = FormatId('D', book.Id);
            if (newIds.Contains(id)) continue;
            var response = updated[id] as JsonObject ?? new JsonObject();
            response["isDefault"] = book.IsDefault;
            response["myRights"] = Rights(book);
            updated[id] = response;
        }
    }

    private static MailAddressBookValues? TryParseCreate(JsonObject value, out JsonObject? issue)
    {
        issue = null;
        var invalid = value.Where(item => item.Key is not
            ("name" or "description" or "sortOrder" or "isSubscribed" or "shareWith"))
            .Select(item => item.Key).ToList();
        if (!TryOptionalString(value, "name", out var name)) invalid.Add("name");
        if (!TryOptionalString(value, "description", out var description)) invalid.Add("description");
        if (!TryUnsigned(value["sortOrder"], out var sortOrder)) invalid.Add("sortOrder");
        if (!TryCreateSubscribed(value, out var subscribed)) invalid.Add("isSubscribed");
        if (invalid.Count > 0)
        {
            issue = InvalidProperties(invalid);
            return null;
        }
        return new(name, description, sortOrder ?? 0, subscribed);
    }

    private static MailAddressBookPatch? TryParsePatch(JsonObject patch, out JsonObject? issue)
    {
        issue = null;
        if (!TryPaths(patch, out var paths))
        {
            issue = new JsonObject { ["type"] = "invalidPatch" };
            return null;
        }
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var rightsFields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (ref readonly var entry in CollectionsMarshal.AsSpan(paths))
        {
            if (entry.Path.Length == 1) fields[entry.Path[0]] = entry.Value;
            else if (entry.Path.Length == 2 && string.Equals(entry.Path[0], "myRights", StringComparison.Ordinal))
                rightsFields[entry.Path[1]] = entry.Value;
            else
            {
                issue = new JsonObject { ["type"] = "invalidPatch" };
                return null;
            }
        }
        var invalid = fields.Where(item => item.Value is not null && item.Key is not
            ("name" or "description" or "sortOrder" or "isSubscribed" or "id" or "isDefault"
                or "shareWith" or "myRights")).Select(item => item.Key).ToList();
        var scalarsValid = TryPatchScalars(fields, out var values, invalid);
        var rightsValid = TryRights(fields.GetValueOrDefault("myRights"), fields.ContainsKey("myRights"),
            rightsFields, out var rights);
        if (!rightsValid) invalid.Add("myRights");
        if (!scalarsValid || invalid.Count > 0)
        {
            issue = InvalidProperties(invalid);
            return null;
        }
        return values! with { Rights = rights };
    }

    private static bool TryPatchScalars(Dictionary<string, JsonNode?> fields,
        out MailAddressBookPatch? patch, List<string> invalid)
    {
        patch = null;
        if (!TryOptionalString(fields, "name", out var name)) invalid.Add("name");
        if (!TryOptionalString(fields, "description", out var description)) invalid.Add("description");
        if (!TryUnsigned(fields.GetValueOrDefault("sortOrder"), out var sortOrder)) invalid.Add("sortOrder");
        if (!TryOptionalBoolean(fields, "isSubscribed", out var subscribed)) invalid.Add("isSubscribed");
        if (!TryOptionalString(fields, "id", out var id)) invalid.Add("id");
        if (!TryOptionalBoolean(fields, "isDefault", out var isDefault)) invalid.Add("isDefault");
        if (invalid.Count > 0) return false;
        patch = new(fields.ContainsKey("name"), name,
            fields.ContainsKey("description"), description,
            fields.ContainsKey("sortOrder"), sortOrder,
            fields.ContainsKey("isSubscribed"), subscribed,
            fields.ContainsKey("id"), id,
            fields.ContainsKey("isDefault"), isDefault,
            null,
            fields.TryGetValue("shareWith", out var shares) && shares is not null);
        return true;
    }

    private static bool TryRights(JsonNode? whole, bool hasWhole,
        Dictionary<string, JsonNode?> partial, out MailAddressBookRightsAssertion? rights)
    {
        rights = null;
        if (!hasWhole && partial.Count == 0) return true;
        if (hasWhole)
        {
            if (whole is not JsonObject objectValue || objectValue.Count != 4
                || objectValue.Any(item => item.Key is not
                    ("mayRead" or "mayWrite" or "mayShare" or "mayDelete"))) return false;
            partial = objectValue.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }
        if (partial.Any(item => item.Key is not ("mayRead" or "mayWrite" or "mayShare" or "mayDelete")
            || !TryBoolean(item.Value, out _))) return false;
        rights = new(partial.ContainsKey("mayRead"), BooleanValue(partial, "mayRead"),
            partial.ContainsKey("mayWrite"), BooleanValue(partial, "mayWrite"),
            partial.ContainsKey("mayShare"), BooleanValue(partial, "mayShare"),
            partial.ContainsKey("mayDelete"), BooleanValue(partial, "mayDelete"));
        return true;
    }

    private static bool? BooleanValue(Dictionary<string, JsonNode?> values, string key) =>
        values.TryGetValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag : null;

    private static bool TryPaths(JsonObject patch, out List<PatchEntry> entries)
    {
        entries = new List<PatchEntry>(patch.Count);
        foreach (var item in patch)
        {
            if (!TryDecodePath(item.Key, out var path)) return false;
            if (entries.Any(existing => IsPrefix(path, existing.Path) || IsPrefix(existing.Path, path)))
                return false;
            entries.Add(new(path, item.Value));
        }
        return true;
    }

    private static bool IsPrefix(string[] left, string[] right)
    {
        if (left.Length > right.Length) return false;
        var index = 0;
        foreach (var part in left)
        {
            if (!string.Equals(part, right[index++], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool TryDecodePath(string value, out string[] path)
    {
        path = [];
        if (value.Length == 0 || value[0] == '/' || value[^1] == '/') return false;
        var pieces = value.Split('/');
        var decoded = new string[pieces.Length];
        var pieceIndex = 0;
        foreach (var piece in pieces)
        {
            var builder = new StringBuilder(piece.Length);
            for (var index = 0; index < piece.Length; index++)
            {
                if (piece[index] != '~')
                {
                    builder.Append(piece[index]);
                    continue;
                }
                if (++index >= piece.Length || piece[index] is not ('0' or '1')) return false;
                builder.Append(piece[index] == '0' ? '~' : '/');
            }
            decoded[pieceIndex] = builder.ToString();
            if (decoded[pieceIndex++].Length == 0) return false;
        }
        path = decoded;
        return true;
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

    private static MailAddressBookTarget ParseTarget(string value, IReadOnlyDictionary<string, string> known)
    {
        if (value.StartsWith('#'))
        {
            var key = value[1..];
            if (known.TryGetValue(key, out var resolved)) value = resolved;
            else return new(null, key);
        }
        return value.Length == 33 && value[0] == 'D'
            && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
            && string.Equals(value, FormatId('D', id), StringComparison.Ordinal)
                ? new(id, null) : new(null, null);
    }

    private static bool TryOptionalString(JsonObject value, string key, out string? result) =>
        TryOptionalString(value.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            key, out result);

    private static bool TryOptionalString(Dictionary<string, JsonNode?> value, string key, out string? result)
    {
        result = null;
        return !value.TryGetValue(key, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue<string>(out result);
    }

    private static bool TryOptionalBoolean(JsonObject value, string key, out bool result)
    {
        result = false;
        return !value.TryGetPropertyValue(key, out var node)
            || node is JsonValue scalar && scalar.TryGetValue<bool>(out result);
    }

    private static bool TryOptionalBoolean(Dictionary<string, JsonNode?> value, string key, out bool? result)
    {
        result = null;
        return !value.TryGetValue(key, out var node) || TryBoolean(node, out result);
    }

    private static bool TryBoolean(JsonNode? node, out bool? result)
    {
        result = null;
        if (node is null) return true;
        if (node is not JsonValue scalar || !scalar.TryGetValue<bool>(out var parsed)) return false;
        result = parsed;
        return true;
    }

    private static bool TryCreateSubscribed(JsonObject value, out bool subscribed)
    {
        subscribed = true;
        return !value.TryGetPropertyValue("isSubscribed", out var node)
            || node is JsonValue scalar && scalar.TryGetValue<bool>(out subscribed);
    }

    private static bool TryUnsigned(JsonNode? node, out long? result)
    {
        result = null;
        if (node is null) return true;
        if (node is not JsonValue scalar) return false;
        if (scalar.TryGetValue<long>(out var signed)) result = signed;
        else if (scalar.TryGetValue<int>(out var small)) result = small;
        else if (scalar.TryGetValue<uint>(out var unsigned)) result = unsigned;
        else if (scalar.TryGetValue<ulong>(out var large) && large <= long.MaxValue)
            result = checked((long)large);
        return result is >= 0 and <= int.MaxValue;
    }

    private static void ValidateBook(MailAddressBookSnapshot book)
    {
        if (book is null || book.Id == Guid.Empty || book.Name is null || book.SortOrder is < 0 or > int.MaxValue)
            throw new InvalidOperationException("The Application returned invalid address-book data.");
    }

    private static JsonObject Rights(MailAddressBookSnapshot book) => new()
    {
        ["mayRead"] = true,
        ["mayWrite"] = true,
        ["mayShare"] = false,
        ["mayDelete"] = !book.IsProtected,
    };

    private static JsonObject InvalidProperties(IEnumerable<string> fields) => new()
    {
        ["type"] = "invalidProperties",
        ["properties"] = new JsonArray(fields.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(field => (JsonNode?)JsonValue.Create(field)).ToArray()),
    };

    private static JsonObject ItemError(MailAddressBookMutationError error,
        IReadOnlyList<MailAddressBookField>? fields = null)
    {
        var type = error switch
        {
            MailAddressBookMutationError.InvalidProperties => "invalidProperties",
            MailAddressBookMutationError.Forbidden => "forbidden",
            MailAddressBookMutationError.OverQuota => "overQuota",
            MailAddressBookMutationError.NotFound => "notFound",
            MailAddressBookMutationError.WillDestroy => "willDestroy",
            MailAddressBookMutationError.AddressBookHasContents => "addressBookHasContents",
            _ => throw new InvalidOperationException("The Application returned an invalid address-book error."),
        };
        var result = new JsonObject { ["type"] = type };
        if (fields is { Count: > 0 } && error == MailAddressBookMutationError.InvalidProperties)
            result["properties"] = new JsonArray(fields.Select(field => (JsonNode?)JsonValue.Create(Field(field)))
                .ToArray());
        return result;
    }

    private static string Field(MailAddressBookField field) => field switch
    {
        MailAddressBookField.Id => "id",
        MailAddressBookField.Name => "name",
        MailAddressBookField.Description => "description",
        MailAddressBookField.SortOrder => "sortOrder",
        MailAddressBookField.IsDefault => "isDefault",
        MailAddressBookField.IsSubscribed => "isSubscribed",
        MailAddressBookField.ShareWith => "shareWith",
        MailAddressBookField.MyRights => "myRights",
        _ => throw new InvalidOperationException("The Application returned an unknown address-book field."),
    };

    private static string DisplayId(string requested, IReadOnlyDictionary<string, string> known,
        IReadOnlyDictionary<string, string> created) => requested.StartsWith('#')
            ? created.GetValueOrDefault(requested[1..], known.GetValueOrDefault(requested[1..], requested))
            : requested;

    private static string FormatId(char prefix, Guid id) => $"{prefix}{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
