using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayContactQueryCodec
{
    private const long MaximumInteger = 9_007_199_254_740_991;

    internal sealed record QueryCall(
        MailContactQueryCommand Command,
        string AccountId,
        string? DeferredError,
        bool CalculateTotal,
        long? RequestedLimit);

    internal sealed record ChangesCall(
        MailContactQueryChangesCommand Command,
        string AccountId,
        string? DeferredError,
        bool CalculateTotal);

    public static bool TryParseQuery(JsonObject arguments, int maximumObjects,
        out QueryCall? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not (
                "accountId" or "filter" or "sort" or "position" or "anchor"
                or "anchorOffset" or "limit" or "calculateTotal"))
            || !TryRequiredString(arguments, "accountId", out var accountId)
            || !TryWindow(arguments, out var position, out var anchor, out var anchorOffset)
            || !TryUnsigned(arguments, "limit", out var requestedLimit)
            || !TryBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            failure = "invalidArguments";
            return false;
        }
        var criteria = ParseCriteria(arguments, out var deferredError);
        var parseable = TryParseAccountId(accountId!, out var parsedAccount);
        var eligible = parseable && string.Equals(accountId, $"A{parsedAccount:N}", StringComparison.Ordinal);
        var parsedAnchor = Guid.Empty;
        var anchorCanMatch = anchor is not null && TryParseCanonicalCardId(anchor, out parsedAnchor);
        Guid? anchorId = anchor is null ? null : anchorCanMatch ? parsedAnchor : Guid.Empty;
        var limit = checked((int)Math.Min(requestedLimit ?? maximumObjects, maximumObjects));
        call = new(new(parsedAccount, parseable, eligible, criteria, deferredError is not null,
                position, anchorId, anchorCanMatch, anchorOffset, limit),
            accountId!, deferredError, calculateTotal, requestedLimit);
        return true;
    }

    public static bool TryParseChanges(JsonObject arguments,
        out ChangesCall? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not (
                "accountId" or "filter" or "sort" or "sinceQueryState"
                or "maxChanges" or "upToId" or "calculateTotal"))
            || !TryRequiredString(arguments, "accountId", out var accountId)
            || !TryRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !TryUnsigned(arguments, "maxChanges", out var maxChanges)
            || !TryOptionalId(arguments, "upToId", out _)
            || !TryBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            failure = "invalidArguments";
            return false;
        }
        var criteria = ParseCriteria(arguments, out var deferredError);
        var parseable = TryParseAccountId(accountId!, out var parsedAccount);
        var eligible = parseable && string.Equals(accountId, $"A{parsedAccount:N}", StringComparison.Ordinal);
        call = new(new(parsedAccount, parseable, eligible, criteria, deferredError is not null,
                sinceState!, maxChanges), accountId!, deferredError, calculateTotal);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderQuery(
        QueryCall call, MailContactQueryResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Ids is null)
            throw new InvalidOperationException("The Application returned an invalid contact query.");
        if (result.Status == MailContactQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailContactQueryStatus.AccountNotSupported)
            return Error("accountNotSupportedByMethod");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailContactQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize a deferred contact query error.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailContactQueryStatus.AnchorNotFound)
            return Error("anchorNotFound");
        if (result.Status != MailContactQueryStatus.Ok || result.State is null
            || result.Position is < 0 or > MaximumInteger || result.Total < 0)
            throw new InvalidOperationException("The Application returned an incomplete contact query.");
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["queryState"] = result.State,
            ["canCalculateChanges"] = true,
            ["position"] = result.Position,
            ["ids"] = ToArray(result.Ids.Select(CardId)),
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        if (call.RequestedLimit is null || call.RequestedLimit > call.Command.Limit)
            data["limit"] = call.Command.Limit;
        return (MailOperationKind.FindContacts, data);
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderChanges(
        ChangesCall call, MailContactQueryChangesResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Removed is null || result.Added is null)
            throw new InvalidOperationException("The Application returned invalid contact query changes.");
        if (result.Status == MailContactQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailContactQueryStatus.AccountNotSupported)
            return Error("accountNotSupportedByMethod");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailContactQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize deferred contact query changes.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailContactQueryStatus.CannotCalculateChanges)
            return Error("cannotCalculateChanges");
        if (result.Status == MailContactQueryStatus.TooManyChanges)
            return Error("tooManyChanges");
        if (result.Status != MailContactQueryStatus.Ok || result.State is null || result.Total < 0
            || result.Removed.Any(id => id is null || !GatewayJmapBatchCodec.IsId(id))
            || result.Added.Any(item => item is null || item.Index < 0))
            throw new InvalidOperationException("The Application returned incomplete contact query changes.");
        var added = new JsonArray();
        foreach (var item in result.Added)
            added.Add(new JsonObject { ["id"] = CardId(item.Id), ["index"] = item.Index });
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldQueryState"] = call.Command.SinceState,
            ["newQueryState"] = result.State,
            ["removed"] = ToArray(result.Removed),
            ["added"] = added,
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        return (MailOperationKind.FindContactChanges, data);
    }

    private static MailContactQueryCriteria ParseCriteria(JsonObject arguments, out string? failure)
    {
        failure = null;
        if (!TryFilter(arguments["filter"], 0, out var filter, out failure))
            return new(null, []);
        if (!TrySort(arguments["sort"], out var sort, out failure))
            return new(null, []);
        return new(filter, sort);
    }

    private static bool TryFilter(JsonNode? node, int depth,
        out MailContactFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (node is null) return true;
        if (node is not JsonObject value || depth > 64)
        {
            failure = "invalidArguments";
            return false;
        }
        return value.ContainsKey("operator")
            ? TryOperatorFilter(value, depth, out filter, out failure)
            : TryConditionFilter(value, out filter, out failure);
    }

    private static bool TryOperatorFilter(JsonObject value, int depth,
        out MailContactFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (!TryRequiredString(value, "operator", out var name)
            || name is not ("AND" or "OR" or "NOT")
            || value["conditions"] is not JsonArray conditions
            || value.Any(item => item.Key is not ("operator" or "conditions")))
        {
            failure = "invalidArguments";
            return false;
        }
        var children = new List<MailContactFilter>(conditions.Count);
        foreach (var item in conditions)
        {
            if (!TryFilter(item, depth + 1, out var child, out failure)) return false;
            children.Add(child ?? MatchAll());
        }
        filter = new(name switch
        {
            "AND" => MailContactFilterOperator.And,
            "OR" => MailContactFilterOperator.Or,
            _ => MailContactFilterOperator.Not,
        }, children.ToArray(), null, null, null, null, null);
        return true;
    }

    private static bool TryConditionFilter(JsonObject value,
        out MailContactFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (value.Any(item => item.Key is not (
                "createdBefore" or "createdAfter" or "updatedBefore" or "updatedAfter")
            && !TryField(item.Key, out _)))
        {
            failure = "unsupportedFilter";
            return false;
        }
        var terms = new List<MailContactFilterTerm>(value.Count);
        DateTime? createdBefore = null;
        DateTime? createdAfter = null;
        DateTime? updatedBefore = null;
        DateTime? updatedAfter = null;
        foreach (var item in value)
        {
            if (item.Key is "createdBefore" or "createdAfter" or "updatedBefore" or "updatedAfter")
            {
                if (item.Value is not JsonValue dateValue
                    || !dateValue.TryGetValue<string>(out var dateText)
                    || !GatewayJmapDateCodec.TryParseUtc(dateText, out var date))
                {
                    failure = "invalidArguments";
                    return false;
                }
                switch (item.Key)
                {
                    case "createdBefore": createdBefore = date; break;
                    case "createdAfter": createdAfter = date; break;
                    case "updatedBefore": updatedBefore = date; break;
                    case "updatedAfter": updatedAfter = date; break;
                }
                continue;
            }
            if (!TryField(item.Key, out var field))
            {
                failure = "unsupportedFilter";
                return false;
            }
            if (item.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)
                || text is null || field == MailContactFilterField.InAddressBook
                    && !GatewayJmapBatchCodec.IsId(text))
            {
                failure = "invalidArguments";
                return false;
            }
            terms.Add(new(field, text));
        }
        filter = new(MailContactFilterOperator.Condition, null, terms.ToArray(),
            createdBefore, createdAfter, updatedBefore, updatedAfter);
        return true;
    }

    private static bool TryField(string name, out MailContactFilterField field)
    {
        field = name switch
        {
            "inAddressBook" => MailContactFilterField.InAddressBook,
            "uid" => MailContactFilterField.Uid,
            "hasMember" => MailContactFilterField.HasMember,
            "kind" => MailContactFilterField.Kind,
            "text" => MailContactFilterField.Text,
            "name" => MailContactFilterField.Name,
            "name/given" => MailContactFilterField.GivenName,
            "name/surname" => MailContactFilterField.Surname,
            "name/surname2" => MailContactFilterField.Surname2,
            "nickname" => MailContactFilterField.Nickname,
            "organization" => MailContactFilterField.Organization,
            "email" => MailContactFilterField.Email,
            "phone" => MailContactFilterField.Phone,
            "onlineService" => MailContactFilterField.OnlineService,
            "address" => MailContactFilterField.Address,
            "note" => MailContactFilterField.Note,
            _ => (MailContactFilterField)(-1),
        };
        return Enum.IsDefined(field);
    }

    private static bool TrySort(JsonNode? node,
        out IReadOnlyList<MailContactSort> sort, out string? failure)
    {
        failure = null;
        sort = [];
        if (node is null) return true;
        if (node is not JsonArray array)
        {
            failure = "invalidArguments";
            return false;
        }
        var parsed = new List<MailContactSort>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject value
                || value.Any(field => field.Key is not ("property" or "isAscending" or "collation"))
                || !TryRequiredString(value, "property", out var property)
                || !TryBoolean(value, "isAscending", true, out var ascending)
                || !TryOptionalString(value, "collation", out var collation, false))
            {
                failure = "invalidArguments";
                return false;
            }
            var field = property switch
            {
                "created" => MailContactSortField.Created,
                "updated" => MailContactSortField.Updated,
                "name/given" => MailContactSortField.GivenName,
                "name/surname" => MailContactSortField.Surname,
                "name/surname2" => MailContactSortField.Surname2,
                _ => (MailContactSortField)(-1),
            };
            if (!Enum.IsDefined(field)
                || field is MailContactSortField.GivenName or MailContactSortField.Surname or MailContactSortField.Surname2
                    && collation is not null && collation is not ("i;ascii-numeric" or "i;ascii-casemap"))
            {
                failure = "unsupportedSort";
                return false;
            }
            parsed.Add(new(field, ascending, collation switch
            {
                "i;ascii-numeric" => MailStringCollation.AsciiNumeric,
                "i;ascii-casemap" => MailStringCollation.AsciiCasemap,
                _ => MailStringCollation.UnicodeCasemap,
            }));
        }
        sort = parsed.ToArray();
        return true;
    }

    private static MailContactFilter MatchAll() =>
        new(MailContactFilterOperator.Condition, null, [], null, null, null, null);

    private static bool TryWindow(JsonObject arguments,
        out long position, out string? anchor, out long anchorOffset)
    {
        position = 0;
        anchorOffset = 0;
        if (!TryOptionalId(arguments, "anchor", out anchor)) return false;
        return anchor is null
            ? TryInteger(arguments, "position", 0, out position)
            : TryInteger(arguments, "anchorOffset", 0, out anchorOffset);
    }

    private static bool TryRequiredString(JsonObject arguments, string key, out string? value)
    {
        value = null;
        return arguments[key] is JsonValue node && node.TryGetValue(out value) && value is not null;
    }

    private static bool TryOptionalString(JsonObject arguments, string key,
        out string? value, bool allowNull)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        return node is null ? allowNull : node is JsonValue scalar && scalar.TryGetValue(out value);
    }

    private static bool TryOptionalId(JsonObject arguments, string key, out string? value)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(key, out var node) || node is null) return true;
        return node is JsonValue scalar && scalar.TryGetValue(out value)
            && value is not null && GatewayJmapBatchCodec.IsId(value);
    }

    private static bool TryBoolean(JsonObject arguments, string key, bool defaultValue, out bool value)
    {
        value = defaultValue;
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        return node is JsonValue scalar && scalar.TryGetValue(out value);
    }

    private static bool TryUnsigned(JsonObject arguments, string key, out long? value)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(key, out var node) || node is null) return true;
        if (node is not JsonValue scalar || !TryReadInteger(scalar, out var parsed)
            || parsed is < 0 or > MaximumInteger) return false;
        value = parsed;
        return true;
    }

    private static bool TryInteger(JsonObject arguments, string key, long fallback, out long value)
    {
        value = fallback;
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        return node is JsonValue scalar && TryReadInteger(scalar, out value)
            && value is >= -MaximumInteger and <= MaximumInteger;
    }

    private static bool TryReadInteger(JsonValue node, out long value)
    {
        if (node.TryGetValue<long>(out value)) return true;
        if (node.TryGetValue<int>(out var signed)) { value = signed; return true; }
        if (node.TryGetValue<uint>(out var unsigned)) { value = unsigned; return true; }
        if (node.TryGetValue<ulong>(out var wide) && wide <= long.MaxValue) { value = checked((long)wide); return true; }
        value = 0;
        return false;
    }

    private static bool TryParseAccountId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == 'A' && Guid.TryParseExact(value.AsSpan(1), "N", out id);
    }

    private static bool TryParseCanonicalCardId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == 'C' && Guid.TryParseExact(value.AsSpan(1), "N", out id)
            && string.Equals(value, CardId(id), StringComparison.Ordinal);
    }

    private static string CardId(Guid id) => $"C{id:N}";

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
