using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapSubmissionComparator(
    string Property,
    bool IsAscending,
    string? Collation);

internal static class JmapEmailSubmissionQueryEngine
{
    public static bool TryFilter(
        IReadOnlyList<JmapEmailSubmissionDB> all,
        JsonNode? node,
        JmapInvocationContext context,
        out List<JmapEmailSubmissionDB> result,
        out string error)
    {
        result = [];
        if (!TryPredicate(node, context, out var predicate, out error))
            return false;
        result = all.Where(predicate).ToList();
        return true;
    }

    public static bool TrySort(
        JsonNode? node,
        out IReadOnlyList<JmapSubmissionComparator> result,
        out string error)
    {
        error = string.Empty;
        if (node is null)
        {
            result = [new JmapSubmissionComparator("sentAt", false, null)];
            return true;
        }
        if (node is not JsonArray array)
        {
            result = [];
            error = "invalidArguments";
            return false;
        }
        var comparators = new List<JmapSubmissionComparator>();
        foreach (var item in array)
        {
            if (item is not JsonObject comparator
                || !JmapMethodHelpers.HasOnlyProperties(
                    comparator,
                    "property",
                    "isAscending",
                    "collation")
                || !JmapMethodHelpers.TryGetRequiredString(comparator, "property", out var property)
                || !JmapMethodHelpers.TryGetOptionalBoolean(comparator, "isAscending", true, out var ascending)
                || !JmapMethodHelpers.TryGetOptionalString(
                    comparator,
                    "collation",
                    out var collation))
            {
                result = [];
                error = "invalidArguments";
                return false;
            }
            if (property is not ("emailId" or "threadId" or "sentAt")
                || property is "emailId" or "threadId"
                    && collation is not null
                    && !JmapCollation.IsSupported(collation))
            {
                result = [];
                error = "unsupportedSort";
                return false;
            }
            comparators.Add(new JmapSubmissionComparator(property, ascending, collation));
        }
        result = comparators;
        return true;
    }

    public static List<JmapEmailSubmissionDB> Sort(
        IEnumerable<JmapEmailSubmissionDB> values,
        IReadOnlyList<JmapSubmissionComparator> comparators)
    {
        var comparer = Comparer<JmapEmailSubmissionDB>.Create((left, right) =>
        {
            foreach (var comparator in comparators)
            {
                var comparison = comparator.Property switch
                {
                    "emailId" => JmapCollation.Compare(left.EmailId, right.EmailId, comparator.Collation),
                    "threadId" => JmapCollation.Compare(left.ThreadId, right.ThreadId, comparator.Collation),
                    "sentAt" => left.SendAt.CompareTo(right.SendAt),
                    _ => 0,
                };
                if (comparison != 0)
                    return comparator.IsAscending ? comparison : -comparison;
            }
            return left.Id.CompareTo(right.Id);
        });
        return values.Order(comparer).ToList();
    }

    private static bool TryPredicate(
        JsonNode? node,
        JmapInvocationContext context,
        out Func<JmapEmailSubmissionDB, bool> predicate,
        out string error)
    {
        predicate = static _ => true;
        error = string.Empty;
        if (node is null) return true;
        if (node is not JsonObject value)
        {
            error = "invalidArguments";
            return false;
        }
        if (value.ContainsKey("operator"))
        {
            if (!JmapMethodHelpers.TryGetRequiredString(value, "operator", out var operation)
                || operation is not ("AND" or "OR" or "NOT")
                || value["conditions"] is not JsonArray conditions
                || value.Any(item => item.Key is not ("operator" or "conditions")))
            {
                error = "invalidArguments";
                return false;
            }
            var children = new List<Func<JmapEmailSubmissionDB, bool>>();
            foreach (var condition in conditions)
            {
                if (!TryPredicate(condition, context, out var child, out error)) return false;
                children.Add(child);
            }
            predicate = operation switch
            {
                "AND" => item => children.All(child => child(item)),
                "OR" => item => children.Any(child => child(item)),
                _ => item => children.All(child => !child(item)),
            };
            return true;
        }
        if (value.Any(item => item.Key is not (
            "identityIds" or "emailIds" or "threadIds" or "undoStatus" or "before" or "after")))
        {
            error = "unsupportedFilter";
            return false;
        }
        if (!TryIds(value, "identityIds", context, out var identityIds)
            || !TryIds(value, "emailIds", context, out var emailIds)
            || !TryIds(value, "threadIds", context, out var threadIds)
            || !JmapMethodHelpers.TryGetOptionalString(
                value,
                "undoStatus",
                out var undoStatus,
                allowNull: false)
            || undoStatus is not null && undoStatus is not ("pending" or "final" or "canceled")
            || !TryDate(value, "before", out var before)
            || !TryDate(value, "after", out var after))
        {
            error = "invalidArguments";
            return false;
        }
        predicate = item =>
            (identityIds is null || identityIds.Contains(item.IdentityId))
            && (emailIds is null || emailIds.Contains(item.EmailId))
            && (threadIds is null || threadIds.Contains(item.ThreadId))
            && (undoStatus is null || item.UndoStatus == undoStatus)
            && (before is null || item.SendAt < before.Value.UtcDateTime)
            && (after is null || item.SendAt >= after.Value.UtcDateTime);
        return true;
    }

    private static bool TryIds(
        JsonObject value,
        string name,
        JmapInvocationContext context,
        out HashSet<string>? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node)) return true;
        if (node is not JsonArray array) return false;
        result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var id)
                || context.ResolveId(id) is not { } resolved) return false;
            result.Add(resolved);
        }
        return true;
    }

    private static bool TryDate(JsonObject value, string name, out DateTimeOffset? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node)) return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text is null
            || !JmapDate.TryParseUtcDate(text, out var parsed))
            return false;
        result = parsed;
        return true;
    }
}

internal sealed class EmailSubmissionQueryMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "EmailSubmission/query";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "filter",
                "sort",
                "position",
                "anchor",
                "anchorOffset",
                "limit",
                "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalInt(arguments, "position", 0, out var position)
            || !JmapMethodHelpers.TryGetOptionalInt(arguments, "anchorOffset", 0, out var anchorOffset)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "limit", out var requestedLimit)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "anchor", out var anchor))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var all = await database.JmapEmailSubmissions.AsNoTracking()
            .Where(item => item.AccountId == account.InboxId)
            .ToListAsync(cancellationToken);
        if (!JmapEmailSubmissionQueryEngine.TryFilter(all, arguments["filter"], context, out var filtered, out var filterError))
            return JmapMethodResponse.Error(filterError);
        if (!JmapEmailSubmissionQueryEngine.TrySort(arguments["sort"], out var sort, out var sortError))
            return JmapMethodResponse.Error(sortError);
        var ids = JmapEmailSubmissionQueryEngine.Sort(filtered, sort)
            .Select(item => JmapId.Submission(item.Id)).ToList();
        if (anchor is not null)
        {
            anchor = context.ResolveId(anchor);
            var anchorIndex = anchor is null ? -1 : ids.IndexOf(anchor);
            if (anchorIndex < 0) return JmapMethodResponse.Error("anchorNotFound");
            position = Math.Min(
                JmapMethodHelpers.MaximumInt,
                Math.Max(0L, anchorIndex + anchorOffset));
        }
        else if (position < 0) position = Math.Max(0L, ids.Count + position);
        var enforcedLimit = JmapMethodHelpers.ClampToServerLimit(
            requestedLimit,
            environment.Jmap.MaxObjectsInGet);
        var pagePosition = position >= ids.Count ? ids.Count : checked((int)position);
        List<string> page = pagePosition >= ids.Count ? [] : ids.Skip(pagePosition).Take(enforcedLimit).ToList();
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["queryState"] = await states.GetStateAsync(account.InboxId, JmapConstants.EmailSubmissionDataType, cancellationToken),
            ["canCalculateChanges"] = false,
            ["position"] = position,
            ["ids"] = JmapMethodHelpers.ToJsonArray(page),
        };
        if (calculateTotal) response["total"] = ids.Count;
        if (requestedLimit is null || requestedLimit > enforcedLimit) response["limit"] = enforcedLimit;
        return new JmapMethodResponse(Name, response);
    }
}

internal sealed class EmailSubmissionQueryChangesMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states) : IJmapMethod
{
    public string Name => "EmailSubmission/queryChanges";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "filter",
                "sort",
                "sinceQueryState",
                "maxChanges",
                "upToId",
                "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var all = await database.JmapEmailSubmissions.AsNoTracking()
            .Where(item => item.AccountId == account.InboxId).ToListAsync(cancellationToken);
        if (!JmapEmailSubmissionQueryEngine.TryFilter(all, arguments["filter"], context, out var filtered, out var filterError))
            return JmapMethodResponse.Error(filterError);
        if (!JmapEmailSubmissionQueryEngine.TrySort(arguments["sort"], out _, out var sortError))
            return JmapMethodResponse.Error(sortError);
        var current = await states.GetStateAsync(account.InboxId, JmapConstants.EmailSubmissionDataType, cancellationToken);
        if (!string.Equals(current, sinceState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("cannotCalculateChanges");
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldQueryState"] = sinceState,
            ["newQueryState"] = current,
            ["removed"] = new JsonArray(),
            ["added"] = new JsonArray(),
        };
        if (calculateTotal) response["total"] = filtered.Count;
        return new JmapMethodResponse(Name, response);
    }
}
