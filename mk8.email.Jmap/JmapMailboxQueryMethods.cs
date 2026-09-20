using System.Text.Json.Nodes;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

internal sealed record JmapMailboxComparator(
    string Property,
    bool IsAscending,
    string? Collation);

internal static class JmapMailboxQueryEngine
{
    public static bool TryFilter(
        IReadOnlyList<JmapMailboxView> allMailboxes,
        JsonNode? filter,
        bool filterAsTree,
        out List<JmapMailboxView> result,
        out string error)
    {
        result = [];
        error = string.Empty;
        if (!TryBuildPredicate(filter, out var predicate, out error))
            return false;

        var matches = allMailboxes
            .Where(predicate)
            .ToDictionary(mailbox => mailbox.Id);
        if (filterAsTree)
        {
            foreach (var mailbox in matches.Values.ToArray())
            {
                var parentId = mailbox.ParentId;
                var visited = new HashSet<Guid>();
                while (parentId is not null)
                {
                    if (!visited.Add(parentId.Value)
                        || !matches.TryGetValue(parentId.Value, out var parent))
                    {
                        matches.Remove(mailbox.Id);
                        break;
                    }
                    parentId = parent.ParentId;
                }
            }
        }

        result = matches.Values.ToList();
        return true;
    }

    public static bool TryParseSort(
        JsonNode? sort,
        out IReadOnlyList<JmapMailboxComparator> comparators,
        out string error)
    {
        error = string.Empty;
        if (sort is null)
        {
            comparators =
            [
                new JmapMailboxComparator("sortOrder", true, null),
                new JmapMailboxComparator("name", true, null),
            ];
            return true;
        }
        if (sort is not JsonArray array)
        {
            comparators = [];
            error = "invalidArguments";
            return false;
        }

        var parsed = new List<JmapMailboxComparator>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject comparator
                || !JmapMethodHelpers.HasOnlyProperties(
                    comparator,
                    "property",
                    "isAscending",
                    "collation")
                || !JmapMethodHelpers.TryGetRequiredString(comparator, "property", out var property)
                || !JmapMethodHelpers.TryGetOptionalBoolean(
                    comparator,
                    "isAscending",
                    true,
                    out var isAscending)
                || !JmapMethodHelpers.TryGetOptionalString(
                    comparator,
                    "collation",
                    out var collation,
                    allowNull: false))
            {
                comparators = [];
                error = "invalidArguments";
                return false;
            }
            if (property is not ("sortOrder" or "name")
                || property == "name"
                    && collation is not null
                    && !JmapCollation.IsSupported(collation))
            {
                comparators = [];
                error = "unsupportedSort";
                return false;
            }
            parsed.Add(new JmapMailboxComparator(property, isAscending, collation));
        }

        comparators = parsed;
        return true;
    }

    public static List<JmapMailboxView> Sort(
        IReadOnlyList<JmapMailboxView> allMailboxes,
        IReadOnlyList<JmapMailboxView> filtered,
        IReadOnlyList<JmapMailboxComparator> comparators,
        bool sortAsTree)
    {
        var comparer = Comparer<JmapMailboxView>.Create((left, right) =>
            Compare(left, right, comparators));
        if (!sortAsTree)
            return filtered.Order(comparer).ToList();

        var included = filtered.Select(mailbox => mailbox.Id).ToHashSet();
        var byParent = allMailboxes.ToLookup(mailbox => mailbox.ParentId);
        var ordered = new List<JmapMailboxView>(allMailboxes.Count);
        var seen = new HashSet<Guid>();

        void Visit(JmapMailboxView mailbox)
        {
            if (!seen.Add(mailbox.Id))
                return;
            if (included.Contains(mailbox.Id))
                ordered.Add(mailbox);
            foreach (var child in byParent[mailbox.Id].Order(comparer))
                Visit(child);
        }

        foreach (var root in byParent[null].Order(comparer))
            Visit(root);
        foreach (var orphan in allMailboxes.Where(mailbox => !seen.Contains(mailbox.Id)).Order(comparer))
            Visit(orphan);
        return ordered;
    }

    private static bool TryBuildPredicate(
        JsonNode? filter,
        out Func<JmapMailboxView, bool> predicate,
        out string error)
    {
        predicate = static _ => true;
        error = string.Empty;
        if (filter is null)
            return true;
        if (filter is not JsonObject value)
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

            var predicates = new List<Func<JmapMailboxView, bool>>(conditions.Count);
            foreach (var condition in conditions)
            {
                if (!TryBuildPredicate(condition, out var childPredicate, out error))
                    return false;
                predicates.Add(childPredicate);
            }
            predicate = operation switch
            {
                "AND" => mailbox => predicates.All(item => item(mailbox)),
                "OR" => mailbox => predicates.Any(item => item(mailbox)),
                _ => mailbox => predicates.All(item => !item(mailbox)),
            };
            return true;
        }

        if (value.Any(item => item.Key is not (
            "parentId" or "name" or "role" or "hasAnyRole" or "isSubscribed")))
        {
            error = "unsupportedFilter";
            return false;
        }

        string? parentId = null;
        var matchNullParent = false;
        if (value.TryGetPropertyValue("parentId", out var parentNode))
        {
            if (parentNode is null)
            {
                matchNullParent = true;
            }
            else if (parentNode is not JsonValue parentValue
                     || !parentValue.TryGetValue<string>(out var parentString)
                     || parentString is null
                     || !JmapId.IsValidId(parentString))
            {
                error = "invalidArguments";
                return false;
            }
            else
            {
                parentId = parentString;
            }
        }

        string? name = null;
        if (value.TryGetPropertyValue("name", out var nameNode)
            && (nameNode is not JsonValue nameValue
                || !nameValue.TryGetValue<string>(out name)
                || name is null))
        {
            error = "invalidArguments";
            return false;
        }

        string? role = null;
        var matchNullRole = false;
        if (value.TryGetPropertyValue("role", out var roleNode))
        {
            if (roleNode is null)
            {
                matchNullRole = true;
            }
            else if (roleNode is not JsonValue roleValue
                     || !roleValue.TryGetValue<string>(out role)
                     || role is null)
            {
                error = "invalidArguments";
                return false;
            }
        }

        bool? hasAnyRole = null;
        if (value.TryGetPropertyValue("hasAnyRole", out var hasRoleNode))
        {
            if (hasRoleNode is not JsonValue hasRoleValue
                || !hasRoleValue.TryGetValue<bool>(out var parsedHasRole))
            {
                error = "invalidArguments";
                return false;
            }
            hasAnyRole = parsedHasRole;
        }

        bool? isSubscribed = null;
        if (value.TryGetPropertyValue("isSubscribed", out var subscribedNode))
        {
            if (subscribedNode is not JsonValue subscribedValue
                || !subscribedValue.TryGetValue<bool>(out var parsedSubscribed))
            {
                error = "invalidArguments";
                return false;
            }
            isSubscribed = parsedSubscribed;
        }

        predicate = mailbox =>
            (!matchNullParent || mailbox.ParentId is null)
            && (parentId is null
                || mailbox.ParentId is { } actualParentId
                    && string.Equals(
                        JmapId.Mailbox(actualParentId),
                        parentId,
                        StringComparison.Ordinal))
            && (name is null || mailbox.Name.Contains(name, StringComparison.InvariantCultureIgnoreCase))
            && (!matchNullRole && role is null || string.Equals(mailbox.Role, role, StringComparison.Ordinal))
            && (!matchNullRole || mailbox.Role is null)
            && (hasAnyRole is null || (mailbox.Role is not null) == hasAnyRole)
            && (isSubscribed is null || mailbox.IsSubscribed == isSubscribed);
        return true;
    }

    private static int Compare(
        JmapMailboxView left,
        JmapMailboxView right,
        IReadOnlyList<JmapMailboxComparator> comparators)
    {
        foreach (var comparator in comparators)
        {
            var comparison = comparator.Property switch
            {
                "sortOrder" => left.SortOrder.CompareTo(right.SortOrder),
                "name" => CompareNames(left.Name, right.Name, comparator.Collation),
                _ => 0,
            };
            if (comparison != 0)
                return comparator.IsAscending ? comparison : -comparison;
        }
        return left.Id.CompareTo(right.Id);
    }

    private static int CompareNames(string left, string right, string? collation) =>
        JmapCollation.Compare(left, right, collation);
}

internal sealed class MailboxQueryMethod(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Mailbox/query";
    public string Capability => JmapConstants.MailCapability;

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
                "calculateTotal",
                "sortAsTree",
                "filterAsTree")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "sortAsTree", false, out var sortAsTree)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "filterAsTree", false, out var filterAsTree)
            || !JmapMethodHelpers.TryGetQueryWindow(
                arguments,
                out var position,
                out var anchor,
                out var anchorOffset)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "limit", out var requestedLimit)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var allMailboxes = await mailboxes.LoadAsync(account.InboxId, cancellationToken);
        if (!JmapMailboxQueryEngine.TryFilter(
                allMailboxes,
                arguments["filter"],
                filterAsTree,
                out var filtered,
                out var filterError))
        {
            return JmapMethodResponse.Error(filterError);
        }
        if (!JmapMailboxQueryEngine.TryParseSort(
                arguments["sort"],
                out var sort,
                out var sortError))
        {
            return JmapMethodResponse.Error(sortError);
        }

        var ordered = JmapMailboxQueryEngine.Sort(allMailboxes, filtered, sort, sortAsTree);
        var ids = ordered.Select(mailbox => JmapId.Mailbox(mailbox.Id)).ToList();
        if (anchor is not null)
        {
            var anchorIndex = ids.IndexOf(anchor);
            if (anchorIndex < 0)
                return JmapMethodResponse.Error("anchorNotFound");
            position = Math.Min(
                JmapMethodHelpers.MaximumInt,
                Math.Max(0L, anchorIndex + anchorOffset));
        }
        else if (position < 0)
        {
            position = Math.Max(0L, ids.Count + position);
        }

        var enforcedLimit = JmapMethodHelpers.ClampToServerLimit(
            requestedLimit,
            environment.Jmap.MaxObjectsInGet);
        var pagePosition = position >= ids.Count ? ids.Count : checked((int)position);
        var page = pagePosition >= ids.Count
            ? []
            : ids.Skip(pagePosition).Take(enforcedLimit).ToList();
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["queryState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.MailboxDataType,
                cancellationToken),
            ["canCalculateChanges"] = !sortAsTree && !filterAsTree,
            ["position"] = position,
            ["ids"] = JmapMethodHelpers.ToJsonArray(page),
        };
        if (calculateTotal)
            response["total"] = ids.Count;
        if (requestedLimit is null || requestedLimit > enforcedLimit)
            response["limit"] = enforcedLimit;
        return new JmapMethodResponse(Name, response);
    }
}

internal sealed class MailboxQueryChangesMethod(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states) : IJmapMethod
{
    public string Name => "Mailbox/queryChanges";
    public string Capability => JmapConstants.MailCapability;

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
            || !JmapMethodHelpers.TryGetOptionalId(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var allMailboxes = await mailboxes.LoadAsync(account.InboxId, cancellationToken);
        if (!JmapMailboxQueryEngine.TryFilter(
                allMailboxes,
                arguments["filter"],
                false,
                out var filtered,
                out var filterError))
        {
            return JmapMethodResponse.Error(filterError);
        }
        if (!JmapMailboxQueryEngine.TryParseSort(
                arguments["sort"],
                out var sort,
                out var sortError))
        {
            return JmapMethodResponse.Error(sortError);
        }

        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.MailboxDataType,
            sinceState,
            null,
            int.MaxValue,
            cancellationToken);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");

        var currentIds = JmapMailboxQueryEngine.Sort(allMailboxes, filtered, sort, false)
            .Select(mailbox => JmapId.Mailbox(mailbox.Id))
            .ToList();
        var currentIdSet = currentIds.ToHashSet(StringComparer.Ordinal);
        var removed = changes.Destroyed
            .Concat(changes.Updated)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var changedCurrentIds = changes.Created
            .Concat(changes.Updated)
            .Where(currentIdSet.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var added = currentIds
            .Select((id, index) => new { Id = id, Index = index })
            .Where(item => changedCurrentIds.Contains(item.Id))
            .ToArray();
        if (maxChanges is not null && removed.LongLength + added.LongLength > maxChanges.Value)
            return JmapMethodResponse.Error("tooManyChanges");

        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldQueryState"] = sinceState,
            ["newQueryState"] = changes.NewState,
            ["removed"] = JmapMethodHelpers.ToJsonArray(removed),
            ["added"] = new JsonArray(added
                .Select(item => (JsonNode)new JsonObject
                {
                    ["id"] = item.Id,
                    ["index"] = item.Index,
                })
                .ToArray()),
        };
        if (calculateTotal)
            response["total"] = filtered.Count;
        return new JmapMethodResponse(Name, response);
    }
}
