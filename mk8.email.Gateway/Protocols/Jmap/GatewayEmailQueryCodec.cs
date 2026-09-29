using System.Globalization;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailQueryCodec
{
    private const long MaximumInteger = 9_007_199_254_740_991;

    internal sealed record QueryCall(
        MailMessageQueryCommand Command,
        string AccountId,
        string? DeferredError,
        bool CalculateTotal,
        long? RequestedLimit);

    internal sealed record ChangesCall(
        MailMessageQueryChangesCommand Command,
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
                or "anchorOffset" or "limit" or "calculateTotal" or "collapseThreads"))
            || !TryRequiredString(arguments, "accountId", out var accountId)
            || !TryBoolean(arguments, "collapseThreads", false, out var collapseThreads)
            || !TryWindow(arguments, out var position, out var anchor, out var anchorOffset)
            || !TryUnsigned(arguments, "limit", out var requestedLimit, true)
            || !TryBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            failure = "invalidArguments";
            return false;
        }
        var criteria = ParseCriteria(arguments, collapseThreads, out var deferredError);
        var limit = checked((int)Math.Min(requestedLimit ?? maximumObjects, maximumObjects));
        call = new(new(ParseAccountOrEmpty(accountId!), criteria, deferredError is not null,
                position, anchor, anchorOffset, limit), accountId!, deferredError,
            calculateTotal, requestedLimit);
        return true;
    }

    public static bool TryParseChanges(JsonObject arguments,
        out ChangesCall? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not (
                "accountId" or "filter" or "sort" or "sinceQueryState"
                or "maxChanges" or "upToId" or "calculateTotal" or "collapseThreads"))
            || !TryRequiredString(arguments, "accountId", out var accountId)
            || !TryRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !TryUnsigned(arguments, "maxChanges", out var maxChanges, true)
            || !TryOptionalId(arguments, "upToId", out _)
            || !TryBoolean(arguments, "collapseThreads", false, out var collapseThreads)
            || !TryBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            failure = "invalidArguments";
            return false;
        }
        var criteria = ParseCriteria(arguments, collapseThreads, out var deferredError);
        call = new(new(ParseAccountOrEmpty(accountId!), criteria,
                deferredError is not null, sinceState!, maxChanges),
            accountId!, deferredError, calculateTotal);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderQuery(
        QueryCall call, MailMessageQueryResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Ids is null)
            throw new InvalidOperationException("The Application returned an invalid message query.");
        if (result.Status == MailMessageQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailMessageQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize deferred message query errors.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailMessageQueryStatus.AnchorNotFound)
            return Error("anchorNotFound");
        if (result.Status != MailMessageQueryStatus.Ok || result.State is null
            || result.Position is < 0 or > MaximumInteger || result.Total < 0
            || result.Ids.Any(id => id == Guid.Empty))
            throw new InvalidOperationException("The Application returned an incomplete message query.");
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["queryState"] = result.State,
            ["canCalculateChanges"] = true,
            ["position"] = result.Position,
            ["ids"] = ToArray(result.Ids.Select(EmailId)),
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        if (call.RequestedLimit is null || call.RequestedLimit > call.Command.Limit)
            data["limit"] = call.Command.Limit;
        return (MailOperationKind.FindMessages, data);
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderChanges(
        ChangesCall call, MailMessageQueryChangesResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Removed is null || result.Added is null)
            throw new InvalidOperationException("The Application returned invalid message query changes.");
        if (result.Status == MailMessageQueryStatus.AccountNotFound)
            return Error("accountNotFound");
        if (call.DeferredError is not null)
        {
            if (result.Status != MailMessageQueryStatus.Authorized)
                throw new InvalidOperationException("The Application did not authorize deferred message query changes.");
            return Error(call.DeferredError);
        }
        if (result.Status == MailMessageQueryStatus.CannotCalculateChanges)
            return Error("cannotCalculateChanges");
        if (result.Status == MailMessageQueryStatus.TooManyChanges)
            return Error("tooManyChanges");
        if (result.Status != MailMessageQueryStatus.Ok || result.State is null || result.Total < 0
            || result.Removed.Any(id => id is null || !GatewayJmapBatchCodec.IsId(id))
            || result.Added.Any(item => item is null || item.Id == Guid.Empty || item.Index < 0))
            throw new InvalidOperationException("The Application returned incomplete message query changes.");
        var added = new JsonArray();
        foreach (var item in result.Added)
            added.Add(new JsonObject { ["id"] = EmailId(item.Id), ["index"] = item.Index });
        var data = new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldQueryState"] = call.Command.SinceState,
            ["newQueryState"] = result.State,
            ["removed"] = ToArray(result.Removed),
            ["added"] = added,
        };
        if (call.CalculateTotal) data["total"] = result.Total;
        return (MailOperationKind.FindMessageChanges, data);
    }

    private static MailMessageQueryCriteria ParseCriteria(
        JsonObject arguments, bool collapseThreads, out string? failure)
    {
        failure = null;
        if (!TryFilter(arguments["filter"], 0, out var filter, out failure))
            return new(null, [], collapseThreads);
        if (!TrySort(arguments["sort"], out var sort, out failure))
            return new(null, [], collapseThreads);
        return new(filter, sort, collapseThreads);
    }

    internal static bool TryParseFilter(JsonNode? node,
        out MailMessageFilter? filter, out string? failure) =>
        TryFilter(node, 0, out filter, out failure);

    private static bool TryFilter(JsonNode? node, int depth,
        out MailMessageFilter? filter, out string? failure)
    {
        filter = null;
        failure = null;
        if (depth > 64)
        {
            failure = "invalidArguments";
            return false;
        }
        if (node is null) return true;
        if (node is not JsonObject value)
        {
            failure = "invalidArguments";
            return false;
        }
        if (value.ContainsKey("operator"))
        {
            if (!TryRequiredString(value, "operator", out var name)
                || name is not ("AND" or "OR" or "NOT")
                || value["conditions"] is not JsonArray conditions
                || value.Any(item => item.Key is not ("operator" or "conditions")))
            {
                failure = "invalidArguments";
                return false;
            }
            var children = new List<MailMessageFilter>();
            foreach (var item in conditions)
            {
                if (!TryFilter(item, depth + 1, out var child, out failure)) return false;
                children.Add(child ?? new(MailMessageFilterOperator.Condition, null, []));
            }
            filter = new(name switch
            {
                "AND" => MailMessageFilterOperator.And,
                "OR" => MailMessageFilterOperator.Or,
                _ => MailMessageFilterOperator.Not,
            }, children, null);
            return true;
        }
        var terms = new List<MailMessageFilterTerm>();
        foreach (var (key, termNode) in value)
        {
            var field = ParseFilterField(key);
            if (!Enum.IsDefined(field))
            {
                failure = "unsupportedFilter";
                return false;
            }
            if (!TryTerm(field, termNode, out var term))
            {
                failure = "invalidArguments";
                return false;
            }
            terms.Add(term!);
        }
        filter = new(MailMessageFilterOperator.Condition, null, terms);
        return true;
    }

    private static MailMessageFilterField ParseFilterField(string key) => key switch
    {
        "inMailbox" => MailMessageFilterField.InMailbox,
        "inMailboxOtherThan" => MailMessageFilterField.InMailboxOtherThan,
        "before" => MailMessageFilterField.Before,
        "after" => MailMessageFilterField.After,
        "minSize" => MailMessageFilterField.MinSize,
        "maxSize" => MailMessageFilterField.MaxSize,
        "allInThreadHaveKeyword" => MailMessageFilterField.AllInThreadHaveKeyword,
        "someInThreadHaveKeyword" => MailMessageFilterField.SomeInThreadHaveKeyword,
        "noneInThreadHaveKeyword" => MailMessageFilterField.NoneInThreadHaveKeyword,
        "hasKeyword" => MailMessageFilterField.HasKeyword,
        "notKeyword" => MailMessageFilterField.NotKeyword,
        "hasAttachment" => MailMessageFilterField.HasAttachment,
        "text" => MailMessageFilterField.Text,
        "from" => MailMessageFilterField.From,
        "to" => MailMessageFilterField.To,
        "cc" => MailMessageFilterField.Cc,
        "bcc" => MailMessageFilterField.Bcc,
        "subject" => MailMessageFilterField.Subject,
        "body" => MailMessageFilterField.Body,
        "header" => MailMessageFilterField.Header,
        _ => (MailMessageFilterField)(-1),
    };

    private static bool TryTerm(MailMessageFilterField field, JsonNode? node,
        out MailMessageFilterTerm? term)
    {
        term = null;
        string? text = null;
        string? headerText = null;
        IReadOnlyList<string>? values = null;
        DateTime? date = null;
        long? number = null;
        bool? flag = null;
        switch (field)
        {
            case MailMessageFilterField.InMailbox:
                if (!TryId(node, out text)) return false;
                break;
            case MailMessageFilterField.InMailboxOtherThan:
                if (!TryIdList(node, out values)) return false;
                break;
            case MailMessageFilterField.Before or MailMessageFilterField.After:
                if (!TryString(node, out var rawDate)
                    || !GatewayJmapDateCodec.TryParseUtc(rawDate, out var parsedDate)) return false;
                date = parsedDate;
                break;
            case MailMessageFilterField.MinSize or MailMessageFilterField.MaxSize:
                if (node is not JsonValue size || !TryInteger(size, out var parsedSize)
                    || parsedSize is < 0 or > MaximumInteger) return false;
                number = parsedSize;
                break;
            case MailMessageFilterField.HasAttachment:
                if (node is not JsonValue boolean || !boolean.TryGetValue<bool>(out var parsedFlag)) return false;
                flag = parsedFlag;
                break;
            case MailMessageFilterField.AllInThreadHaveKeyword or MailMessageFilterField.SomeInThreadHaveKeyword
                or MailMessageFilterField.NoneInThreadHaveKeyword or MailMessageFilterField.HasKeyword
                or MailMessageFilterField.NotKeyword:
                if (!TryKeyword(node, out text)) return false;
                break;
            case MailMessageFilterField.Header:
                if (!TryHeader(node, out text, out headerText)) return false;
                break;
            default:
                if (!TryString(node, out text)) return false;
                break;
        }
        term = new(field, text, headerText, values, date, number, flag);
        return true;
    }

    private static bool TryIdList(JsonNode? node, out IReadOnlyList<string>? values)
    {
        values = null;
        if (node is not JsonArray ids) return false;
        var parsedIds = new List<string>();
        foreach (var id in ids)
        {
            if (!TryId(id, out var parsed)) return false;
            parsedIds.Add(parsed!);
        }
        values = parsedIds.ToArray();
        return true;
    }

    private static bool TryKeyword(JsonNode? node, out string? value)
    {
        if (!TryString(node, out value)) return false;
        value = CultureInfo.InvariantCulture.TextInfo.ToLower(value!);
        return IsValidKeyword(value);
    }

    private static bool TryHeader(JsonNode? node, out string? name, out string? text)
    {
        name = null;
        text = null;
        return node is JsonArray { Count: 1 or 2 } header
            && TryString(header[0], out name) && !string.IsNullOrEmpty(name)
            && name.All(character => character is >= (char)33 and <= (char)126 && character != ':')
            && (header.Count == 1 || TryString(header[1], out text));
    }

    private static bool TrySort(JsonNode? node,
        out IReadOnlyList<MailMessageSort> sort, out string? failure)
    {
        failure = null;
        sort = [new(MailMessageSortField.ReceivedAt, false, null, MailStringCollation.UnicodeCasemap)];
        if (node is null) return true;
        if (node is not JsonArray array)
        {
            failure = "invalidArguments";
            return false;
        }
        var result = new List<MailMessageSort>();
        foreach (var item in array)
        {
            if (!TryComparator(item, out var comparator, out failure)) return false;
            result.Add(comparator!);
        }
        sort = result.ToArray();
        return true;
    }

    private static bool TryComparator(JsonNode? item,
        out MailMessageSort? comparator, out string? failure)
    {
        comparator = null;
        failure = null;
        if (item is not JsonObject value
            || value.Any(field => field.Key is not ("property" or "isAscending" or "keyword" or "collation"))
            || !TryRequiredString(value, "property", out var property)
            || !TryBoolean(value, "isAscending", true, out var ascending)
            || !TryOptionalString(value, "keyword", out var keyword)
            || !TryOptionalString(value, "collation", out var collation))
        {
            failure = "invalidArguments";
            return false;
        }
        var field = ParseSortField(property!);
        if (!Enum.IsDefined(field))
        {
            failure = "unsupportedSort";
            return false;
        }
        var needsKeyword = field is MailMessageSortField.HasKeyword
            or MailMessageSortField.AllInThreadHaveKeyword or MailMessageSortField.SomeInThreadHaveKeyword;
        if (needsKeyword != (keyword is not null))
        {
            failure = "invalidArguments";
            return false;
        }
        if (keyword is not null)
        {
            keyword = CultureInfo.InvariantCulture.TextInfo.ToLower(keyword);
            if (!IsValidKeyword(keyword))
            {
                failure = "invalidArguments";
                return false;
            }
        }
        if (field is MailMessageSortField.From or MailMessageSortField.To or MailMessageSortField.Subject
            && collation is not null && collation is not ("i;ascii-numeric" or "i;ascii-casemap"))
        {
            failure = "unsupportedSort";
            return false;
        }
        comparator = new(field, ascending, keyword, collation switch
        {
            "i;ascii-numeric" => MailStringCollation.AsciiNumeric,
            "i;ascii-casemap" => MailStringCollation.AsciiCasemap,
            _ => MailStringCollation.UnicodeCasemap,
        });
        return true;
    }

    private static MailMessageSortField ParseSortField(string property) => property switch
    {
        "receivedAt" => MailMessageSortField.ReceivedAt,
        "size" => MailMessageSortField.Size,
        "from" => MailMessageSortField.From,
        "to" => MailMessageSortField.To,
        "subject" => MailMessageSortField.Subject,
        "sentAt" => MailMessageSortField.SentAt,
        "hasKeyword" => MailMessageSortField.HasKeyword,
        "allInThreadHaveKeyword" => MailMessageSortField.AllInThreadHaveKeyword,
        "someInThreadHaveKeyword" => MailMessageSortField.SomeInThreadHaveKeyword,
        _ => (MailMessageSortField)(-1),
    };

    private static bool TryWindow(JsonObject arguments,
        out long position, out string? anchor, out long anchorOffset)
    {
        position = 0;
        anchorOffset = 0;
        if (!TryOptionalId(arguments, "anchor", out anchor)) return false;
        return anchor is null
            ? TryIntegerProperty(arguments, "position", out position)
            : TryIntegerProperty(arguments, "anchorOffset", out anchorOffset);
    }

    private static bool TryRequiredString(JsonObject value, string key, out string? result) =>
        TryString(value[key], out result);

    private static bool TryString(JsonNode? node, out string? result)
    {
        result = null;
        return node is JsonValue value && value.TryGetValue(out result) && result is not null;
    }

    private static bool TryOptionalString(JsonObject value, string key, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(key, out var node) || TryString(node, out result);
    }

    private static bool TryId(JsonNode? node, out string? result) =>
        TryString(node, out result) && GatewayJmapBatchCodec.IsId(result!);

    private static bool TryOptionalId(JsonObject value, string key, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(key, out var node) || node is null || TryId(node, out result);
    }

    private static bool TryBoolean(JsonObject value, string key, bool fallback, out bool result)
    {
        result = fallback;
        return !value.TryGetPropertyValue(key, out var node)
            || node is JsonValue scalar && scalar.TryGetValue(out result);
    }

    private static bool TryUnsigned(JsonObject value, string key, out long? result, bool allowNull)
    {
        result = null;
        if (!value.TryGetPropertyValue(key, out var node)) return true;
        if (node is null) return allowNull;
        if (node is not JsonValue scalar || !TryInteger(scalar, out var parsed)
            || parsed is < 0 or > MaximumInteger) return false;
        result = parsed;
        return true;
    }

    private static bool TryIntegerProperty(JsonObject value, string key, out long result)
    {
        result = 0;
        return !value.TryGetPropertyValue(key, out var node)
            || node is JsonValue scalar && TryInteger(scalar, out result)
                && result is >= -MaximumInteger and <= MaximumInteger;
    }

    private static bool TryInteger(JsonValue scalar, out long result)
    {
        if (scalar.TryGetValue(out result)) return true;
        if (scalar.TryGetValue<int>(out var signed)) { result = signed; return true; }
        if (scalar.TryGetValue<uint>(out var unsigned)) { result = unsigned; return true; }
        if (scalar.TryGetValue<ulong>(out var wide) && wide <= long.MaxValue)
        {
            result = checked((long)wide);
            return true;
        }
        result = 0;
        return false;
    }

    private static bool IsValidKeyword(string keyword) =>
        keyword.Length is >= 1 and <= 255
        && !string.Equals(keyword, "$recent", StringComparison.OrdinalIgnoreCase)
        && keyword.All(character => character is >= (char)0x21 and <= (char)0x7e
            && character is not ('(' or ')' or '{' or ']' or '%' or '*' or '"' or '\\'));

    private static Guid ParseAccountOrEmpty(string value) =>
        value.Length == 33 && value[0] == 'A'
            && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
                ? id : Guid.Empty;

    private static string EmailId(Guid id) => $"E{id:N}";

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
