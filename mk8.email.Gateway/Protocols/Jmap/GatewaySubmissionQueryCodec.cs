using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewaySubmissionQueryCodec
{
    private const long MaximumInteger = 9_007_199_254_740_991;

    internal sealed record QueryCall(
        MailSubmissionQueryCommand Command,
        string AccountId,
        string? DeferredError,
        bool CalculateTotal,
        long? RequestedLimit);

    internal sealed record ChangesCall(
        MailSubmissionQueryChangesCommand Command,
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
        var limit = checked((int)Math.Min(requestedLimit ?? maximumObjects, maximumObjects));
        var parsedAnchor = Guid.Empty;
        var anchorCanMatch = anchor is not null && TryParseCanonicalSubmissionId(anchor, out parsedAnchor);
        Guid? anchorId = anchor is null ? null : anchorCanMatch ? parsedAnchor : Guid.Empty;
        call = new(new(ParseAccountOrEmpty(accountId!), criteria,
                deferredError is not null, position, anchorId, anchorCanMatch, anchorOffset, limit),
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
        call = new(new(ParseAccountOrEmpty(accountId!), criteria,
                deferredError is not null, sinceState!, maxChanges),
            accountId!, deferredError, calculateTotal);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderQuery(QueryCall call,
        MailSubmissionQueryResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Ids is null)
            throw new InvalidOperationException("The Application returned an invalid submission query.");
        if (result.Status == MailSubmissionQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailSubmissionQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize a deferred query error.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailSubmissionQueryStatus.AnchorNotFound)
            return Error("anchorNotFound");
        if (result.Status != MailSubmissionQueryStatus.Ok || result.State is null
            || result.Position is < 0 or > MaximumInteger || result.Total < 0)
            throw new InvalidOperationException("The Application returned an incomplete submission query.");
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["queryState"] = result.State,
            ["canCalculateChanges"] = true,
            ["position"] = result.Position,
            ["ids"] = ToArray(result.Ids.Select(SubmissionId)),
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        if (call.RequestedLimit is null || call.RequestedLimit > call.Command.Limit)
            data["limit"] = call.Command.Limit;
        return (MailOperationKind.FindSubmissions, data);
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderChanges(ChangesCall call,
        MailSubmissionQueryChangesResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Removed is null || result.Added is null)
            throw new InvalidOperationException("The Application returned invalid submission query changes.");
        if (result.Status == MailSubmissionQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailSubmissionQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize deferred query changes.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailSubmissionQueryStatus.CannotCalculateChanges)
            return Error("cannotCalculateChanges");
        if (result.Status == MailSubmissionQueryStatus.TooManyChanges)
            return Error("tooManyChanges");
        if (result.Status != MailSubmissionQueryStatus.Ok || result.State is null || result.Total < 0
            || result.Removed.Any(id => id is null || !GatewayJmapBatchCodec.IsId(id))
            || result.Added.Any(item => item is null || item.Index < 0))
            throw new InvalidOperationException("The Application returned incomplete submission query changes.");
        var added = new JsonArray();
        foreach (var item in result.Added)
            added.Add(new JsonObject { ["id"] = SubmissionId(item.Id), ["index"] = item.Index });
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldQueryState"] = call.Command.SinceState,
            ["newQueryState"] = result.State,
            ["removed"] = ToArray(result.Removed),
            ["added"] = added,
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        return (MailOperationKind.FindSubmissionChanges, data);
    }

    private static MailSubmissionQueryCriteria ParseCriteria(JsonObject arguments, out string? failure)
    {
        failure = null;
        if (!TryFilter(arguments["filter"], out var filter, out failure))
            return new(null, []);
        if (!TrySort(arguments["sort"], out var sort, out failure))
            return new(null, []);
        return new(filter, sort);
    }

    private static bool TryFilter(JsonNode? node, out MailSubmissionFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (node is null) return true;
        if (node is not JsonObject value)
        {
            failure = "invalidArguments";
            return false;
        }
        return value.ContainsKey("operator")
            ? TryOperatorFilter(value, out filter, out failure)
            : TryConditionFilter(value, out filter, out failure);
    }

    private static bool TryOperatorFilter(JsonObject value,
        out MailSubmissionFilter? filter, out string? failure)
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
        var children = new List<MailSubmissionFilter>();
        foreach (var item in conditions)
        {
            if (!TryFilter(item, out var child, out failure)) return false;
            children.Add(child ?? MatchAll());
        }
        var operation = name switch
        {
            "AND" => MailSubmissionFilterOperator.And,
            "OR" => MailSubmissionFilterOperator.Or,
            _ => MailSubmissionFilterOperator.Not,
        };
        filter = new(operation, children.ToArray(), null, null, null, null, null, null);
        return true;
    }

    private static bool TryConditionFilter(JsonObject value,
        out MailSubmissionFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (value.Any(item => item.Key is not (
                "identityIds" or "emailIds" or "threadIds" or "undoStatus" or "before" or "after")))
        {
            failure = "unsupportedFilter";
            return false;
        }
        if (!TryIds(value, "identityIds", out var identityIds)
            || !TryIds(value, "emailIds", out var emailIds)
            || !TryIds(value, "threadIds", out var threadIds)
            || !TryOptionalString(value, "undoStatus", out var status, false)
            || status is not null && status is not ("pending" or "final" or "canceled")
            || !TryDate(value, "before", out var before)
            || !TryDate(value, "after", out var after))
        {
            failure = "invalidArguments";
            return false;
        }
        filter = new(MailSubmissionFilterOperator.Condition, null,
            identityIds, emailIds, threadIds, status, before, after);
        return true;
    }

    private static bool TrySort(JsonNode? node,
        out IReadOnlyList<MailSubmissionSort> sort, out string? failure)
    {
        failure = null;
        sort = [new(MailSubmissionSortField.SentAt, false, MailStringCollation.UnicodeCasemap)];
        if (node is null) return true;
        if (node is not JsonArray array)
        {
            failure = "invalidArguments";
            return false;
        }
        var comparators = new List<MailSubmissionSort>(array.Count);
        foreach (var item in array)
        {
            if (!TryComparator(item, out var comparator, out failure)) return false;
            comparators.Add(comparator!);
        }
        sort = comparators.ToArray();
        return true;
    }

    private static bool TryComparator(JsonNode? node,
        out MailSubmissionSort? comparator, out string? failure)
    {
        comparator = null;
        failure = null;
        if (node is not JsonObject value
            || value.Any(item => item.Key is not ("property" or "isAscending" or "collation"))
            || !TryRequiredString(value, "property", out var property)
            || !TryBoolean(value, "isAscending", true, out var ascending)
            || !TryOptionalString(value, "collation", out var collation, false))
        {
            failure = "invalidArguments";
            return false;
        }
        var field = property switch
        {
            "emailId" => MailSubmissionSortField.EmailId,
            "threadId" => MailSubmissionSortField.ThreadId,
            "sentAt" => MailSubmissionSortField.SentAt,
            _ => (MailSubmissionSortField)(-1),
        };
        if (!Enum.IsDefined(field)
            || field is MailSubmissionSortField.EmailId or MailSubmissionSortField.ThreadId
                && collation is not null && collation is not ("i;ascii-numeric" or "i;ascii-casemap"))
        {
            failure = "unsupportedSort";
            return false;
        }
        var selected = collation switch
        {
            "i;ascii-numeric" => MailStringCollation.AsciiNumeric,
            "i;ascii-casemap" => MailStringCollation.AsciiCasemap,
            _ => MailStringCollation.UnicodeCasemap,
        };
        comparator = new(field, ascending, selected);
        return true;
    }

    private static MailSubmissionFilter MatchAll() =>
        new(MailSubmissionFilterOperator.Condition, null, null, null, null, null, null, null);

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

    private static bool TryIds(JsonObject arguments, string key, out string[]? ids)
    {
        ids = null;
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        if (node is not JsonArray array) return false;
        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id)) return false;
            values.Add(id);
        }
        ids = values.ToArray();
        return true;
    }

    private static bool TryDate(JsonObject arguments, string key, out DateTime? date)
    {
        date = null;
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !GatewayJmapDateCodec.TryParseUtc(text, out var parsed)) return false;
        date = parsed;
        return true;
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

    private static Guid ParseAccountOrEmpty(string value) =>
        value.Length == 33 && value[0] == 'A' && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
            ? id : Guid.Empty;

    private static bool TryParseCanonicalSubmissionId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == 'S' && Guid.TryParseExact(value.AsSpan(1), "N", out id)
            && string.Equals(value, SubmissionId(id), StringComparison.Ordinal);
    }

    private static string SubmissionId(Guid id) => $"S{id:N}";

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
