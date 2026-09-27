using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapEmailSubmissionQueryEngine
{
    public static bool TryFilter(
        IReadOnlyList<JmapEmailSubmissionDB> all,
        JsonNode? node,
        out List<JmapEmailSubmissionDB> result,
        out string error)
    {
        result = [];
        if (!TryPredicate(node, out var predicate, out error))
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
                    out var collation,
                    allowNull: false))
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
                if (!TryPredicate(condition, out var child, out error)) return false;
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
        if (!TryIds(value, "identityIds", out var identityIds)
            || !TryIds(value, "emailIds", out var emailIds)
            || !TryIds(value, "threadIds", out var threadIds)
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
                || id is null
                || !JmapId.IsValidId(id)) return false;
            result.Add(id);
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
