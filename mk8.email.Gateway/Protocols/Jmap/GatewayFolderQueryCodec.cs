using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayFolderQueryCodec
{
    private const long MaximumInteger = 9_007_199_254_740_991;

    internal sealed record QueryCall(
        MailFolderQueryCommand Command,
        string AccountId,
        string? DeferredError,
        bool CalculateTotal,
        long? RequestedLimit);

    internal sealed record ChangesCall(
        MailFolderQueryChangesCommand Command,
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
                or "anchorOffset" or "limit" or "calculateTotal" or "sortAsTree" or "filterAsTree"))
            || !TryRequiredString(arguments, "accountId", out var accountId)
            || !TryBoolean(arguments, "sortAsTree", false, out var sortAsTree)
            || !TryBoolean(arguments, "filterAsTree", false, out var filterAsTree)
            || !TryWindow(arguments, out var position, out var anchor, out var anchorOffset)
            || !TryUnsigned(arguments, "limit", out var requestedLimit)
            || !TryBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            failure = "invalidArguments";
            return false;
        }
        var criteria = ParseCriteria(arguments, sortAsTree, filterAsTree, out var deferredError);
        var limit = checked((int)Math.Min(requestedLimit ?? maximumObjects, maximumObjects));
        var parsedAnchor = Guid.Empty;
        var anchorCanMatch = anchor is not null && TryParseCanonicalFolderId(anchor, out parsedAnchor);
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
        var criteria = ParseCriteria(arguments, false, false, out var deferredError);
        call = new(new(ParseAccountOrEmpty(accountId!), criteria,
                deferredError is not null, sinceState!, maxChanges),
            accountId!, deferredError, calculateTotal);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderQuery(QueryCall call,
        MailFolderQueryResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Ids is null)
            throw new InvalidOperationException("The Application returned an invalid folder query.");
        if (result.Status == MailFolderQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailFolderQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize a deferred folder query error.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailFolderQueryStatus.AnchorNotFound)
            return Error("anchorNotFound");
        if (result.Status != MailFolderQueryStatus.Ok || result.State is null
            || result.Position is < 0 or > MaximumInteger || result.Total < 0)
            throw new InvalidOperationException("The Application returned an incomplete folder query.");
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["queryState"] = result.State,
            ["canCalculateChanges"] = !call.Command.Criteria.SortAsTree && !call.Command.Criteria.FilterAsTree,
            ["position"] = result.Position,
            ["ids"] = ToArray(result.Ids.Select(FolderId)),
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        if (call.RequestedLimit is null || call.RequestedLimit > call.Command.Limit)
            data["limit"] = call.Command.Limit;
        return (MailOperationKind.FindFolders, data);
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderChanges(ChangesCall call,
        MailFolderQueryChangesResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Removed is null || result.Added is null)
            throw new InvalidOperationException("The Application returned invalid folder query changes.");
        if (result.Status == MailFolderQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailFolderQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize deferred folder query changes.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailFolderQueryStatus.CannotCalculateChanges)
            return Error("cannotCalculateChanges");
        if (result.Status == MailFolderQueryStatus.TooManyChanges)
            return Error("tooManyChanges");
        if (result.Status != MailFolderQueryStatus.Ok || result.State is null || result.Total < 0
            || result.Removed.Any(id => id is null || !GatewayJmapBatchCodec.IsId(id))
            || result.Added.Any(item => item is null || item.Index < 0))
            throw new InvalidOperationException("The Application returned incomplete folder query changes.");
        var added = new JsonArray();
        foreach (var item in result.Added)
            added.Add(new JsonObject { ["id"] = FolderId(item.Id), ["index"] = item.Index });
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldQueryState"] = call.Command.SinceState,
            ["newQueryState"] = result.State,
            ["removed"] = ToArray(result.Removed),
            ["added"] = added,
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        return (MailOperationKind.FindFolderChanges, data);
    }

    private static MailFolderQueryCriteria ParseCriteria(JsonObject arguments,
        bool sortAsTree, bool filterAsTree, out string? failure)
    {
        failure = null;
        if (!TryFilter(arguments["filter"], out var filter, out failure))
            return new(null, [], sortAsTree, filterAsTree);
        if (!TrySort(arguments["sort"], out var sort, out failure))
            return new(null, [], sortAsTree, filterAsTree);
        return new(filter, sort, sortAsTree, filterAsTree);
    }

    private static bool TryFilter(JsonNode? node, out MailFolderFilter? filter, out string? failure)
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
        out MailFolderFilter? filter, out string? failure)
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
        var children = new List<MailFolderFilter>();
        foreach (var item in conditions)
        {
            if (!TryFilter(item, out var child, out failure)) return false;
            children.Add(child ?? MatchAll());
        }
        var operation = name switch
        {
            "AND" => MailFolderFilterOperator.And,
            "OR" => MailFolderFilterOperator.Or,
            _ => MailFolderFilterOperator.Not,
        };
        filter = new(operation, children.ToArray(), MailFolderParentConstraint.Any,
            null, null, null, false, null, null);
        return true;
    }

    private static bool TryConditionFilter(JsonObject value,
        out MailFolderFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (value.Any(item => item.Key is not (
                "parentId" or "name" or "role" or "hasAnyRole" or "isSubscribed")))
        {
            failure = "unsupportedFilter";
            return false;
        }
        var parentConstraint = MailFolderParentConstraint.Any;
        Guid? parentId = null;
        if (value.TryGetPropertyValue("parentId", out var parentNode))
        {
            if (parentNode is null)
                parentConstraint = MailFolderParentConstraint.Root;
            else if (parentNode is not JsonValue parentValue
                || !parentValue.TryGetValue<string>(out var parentText)
                || parentText is null || !GatewayJmapBatchCodec.IsId(parentText))
            {
                failure = "invalidArguments";
                return false;
            }
            else if (TryParseCanonicalFolderId(parentText, out var parsedParent))
            {
                parentConstraint = MailFolderParentConstraint.Folder;
                parentId = parsedParent;
            }
            else
                parentConstraint = MailFolderParentConstraint.Impossible;
        }
        if (!TryOptionalString(value, "name", out var name, false)
            || !TryOptionalRole(value, out var role, out var matchNullRole)
            || !TryOptionalBoolean(value, "hasAnyRole", out var hasAnyRole)
            || !TryOptionalBoolean(value, "isSubscribed", out var isSubscribed))
        {
            failure = "invalidArguments";
            return false;
        }
        filter = new(MailFolderFilterOperator.Condition, null, parentConstraint,
            parentId, name, role, matchNullRole, hasAnyRole, isSubscribed);
        return true;
    }

    private static bool TrySort(JsonNode? node,
        out IReadOnlyList<MailFolderSort> sort, out string? failure)
    {
        failure = null;
        sort = [new(MailFolderSortField.SortOrder, true, MailStringCollation.UnicodeCasemap),
            new(MailFolderSortField.Name, true, MailStringCollation.UnicodeCasemap)];
        if (node is null) return true;
        if (node is not JsonArray array)
        {
            failure = "invalidArguments";
            return false;
        }
        var parsed = new List<MailFolderSort>(array.Count);
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
                "sortOrder" => MailFolderSortField.SortOrder,
                "name" => MailFolderSortField.Name,
                _ => (MailFolderSortField)(-1),
            };
            if (!Enum.IsDefined(field)
                || field == MailFolderSortField.Name && collation is not null
                    && collation is not ("i;ascii-numeric" or "i;ascii-casemap"))
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

    private static MailFolderFilter MatchAll() =>
        new(MailFolderFilterOperator.Condition, null, MailFolderParentConstraint.Any,
            null, null, null, false, null, null);

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

    private static bool TryOptionalRole(JsonObject arguments,
        out string? role, out bool matchNullRole)
    {
        role = null;
        matchNullRole = false;
        if (!arguments.TryGetPropertyValue("role", out var node)) return true;
        if (node is null) { matchNullRole = true; return true; }
        return node is JsonValue scalar && scalar.TryGetValue(out role) && role is not null;
    }

    private static bool TryOptionalBoolean(JsonObject arguments, string key, out bool? value)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        if (node is not JsonValue scalar || !scalar.TryGetValue<bool>(out var parsed)) return false;
        value = parsed;
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

    private static bool TryParseCanonicalFolderId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == 'M' && Guid.TryParseExact(value.AsSpan(1), "N", out id)
            && string.Equals(value, FolderId(id), StringComparison.Ordinal);
    }

    private static string FolderId(Guid id) => $"M{id:N}";

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
