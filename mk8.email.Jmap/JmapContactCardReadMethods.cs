using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

internal sealed class ContactCardGetMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "ContactCard/get";
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetIdArray(arguments, "ids", true, out var requestedIds)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var propertyList))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        if (propertyList is not null
            && propertyList.Any(property => !JmapContactValidator.IsSupportedCardProperty(property)))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken);
        var cards = await contacts.LoadCardsAsync(account.UserId, false, cancellationToken);
        if (requestedIds is null && cards.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
        var properties = propertyList?.ToHashSet(StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var card))
                list.Add(JmapContactStore.BuildContactCard(card, properties));
            else
                notFound.Add(id);
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.ContactCardDataType,
                cancellationToken),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}

internal sealed class ContactCardChangesMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "ContactCard/changes";
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "sinceState", "maxChanges")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken);
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.ContactCardDataType,
            sinceState,
            maxChanges,
            environment.Jmap.MaxObjectsInGet,
            cancellationToken);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = changes.OldState,
            ["newState"] = changes.NewState,
            ["hasMoreChanges"] = changes.HasMoreChanges,
            ["created"] = JmapMethodHelpers.ToJsonArray(changes.Created),
            ["updated"] = JmapMethodHelpers.ToJsonArray(changes.Updated),
            ["destroyed"] = JmapMethodHelpers.ToJsonArray(changes.Destroyed),
            ["updatedProperties"] = null,
        });
    }
}

internal sealed record JmapContactComparator(
    string Property,
    bool IsAscending,
    string? Collation);

internal static class JmapContactQueryEngine
{
    private static readonly IReadOnlySet<string> FilterProperties = new HashSet<string>(
        [
            "inAddressBook", "uid", "hasMember", "kind", "createdBefore", "createdAfter",
            "updatedBefore", "updatedAfter", "text", "name", "name/given", "name/surname",
            "name/surname2", "nickname", "organization", "email", "phone", "onlineService",
            "address", "note",
        ],
        StringComparer.Ordinal);

    public static bool TryFilter(
        IReadOnlyList<JmapContactCardView> cards,
        JsonNode? filter,
        out List<JmapContactCardView> result,
        out string error)
    {
        result = [];
        if (!TryBuildPredicate(filter, out var predicate, out error))
            return false;
        result = cards.Where(predicate).ToList();
        return true;
    }

    public static bool IsFilterMutable(JsonNode? filter)
    {
        if (filter is not JsonObject value)
            return false;
        if (!value.ContainsKey("operator"))
            return value.Count > 0;
        return value["conditions"] is JsonArray conditions
            && conditions.Any(IsFilterMutable);
    }

    public static bool ImmutableFilterMatchesAll(JsonNode? filter)
    {
        if (filter is not JsonObject value || !value.ContainsKey("operator"))
            return true;
        var operation = value["operator"]!.GetValue<string>();
        var conditions = value["conditions"]!.AsArray()
            .Select(ImmutableFilterMatchesAll)
            .ToArray();
        return operation switch
        {
            "AND" => conditions.All(result => result),
            "OR" => conditions.Any(result => result),
            _ => conditions.All(result => !result),
        };
    }

    public static bool TryParseSort(
        JsonNode? sort,
        out IReadOnlyList<JmapContactComparator> comparators,
        out string error)
    {
        error = string.Empty;
        if (sort is null)
        {
            comparators = [];
            return true;
        }
        if (sort is not JsonArray array)
        {
            comparators = [];
            error = "invalidArguments";
            return false;
        }
        var result = new List<JmapContactComparator>();
        foreach (var node in array)
        {
            if (node is not JsonObject comparator
                || !JmapMethodHelpers.HasOnlyProperties(
                    comparator, "property", "isAscending", "collation")
                || !JmapMethodHelpers.TryGetRequiredString(comparator, "property", out var property)
                || !JmapMethodHelpers.TryGetOptionalBoolean(
                    comparator, "isAscending", true, out var ascending)
                || !JmapMethodHelpers.TryGetOptionalString(
                    comparator, "collation", out var collation, allowNull: false))
            {
                comparators = [];
                error = "invalidArguments";
                return false;
            }
            if (property is not ("created" or "updated" or "name/given" or "name/surname" or "name/surname2")
                || property.StartsWith("name/", StringComparison.Ordinal)
                    && collation is not null
                    && !JmapCollation.IsSupported(collation))
            {
                comparators = [];
                error = "unsupportedSort";
                return false;
            }
            result.Add(new JmapContactComparator(property, ascending, collation));
        }
        comparators = result;
        return true;
    }

    public static List<JmapContactCardView> Sort(
        IEnumerable<JmapContactCardView> cards,
        IReadOnlyList<JmapContactComparator> comparators)
    {
        var comparer = Comparer<JmapContactCardView>.Create((left, right) =>
        {
            foreach (var comparator in comparators)
            {
                var comparison = comparator.Property switch
                {
                    "created" => CardDate(left, "created", left.Resource.CreatedAt)
                        .CompareTo(CardDate(right, "created", right.Resource.CreatedAt)),
                    "updated" => CardDate(left, "updated", left.Resource.UpdatedAt)
                        .CompareTo(CardDate(right, "updated", right.Resource.UpdatedAt)),
                    _ => JmapCollation.Compare(
                        NameComponent(left.Card, comparator.Property[5..]),
                        NameComponent(right.Card, comparator.Property[5..]),
                        comparator.Collation),
                };
                if (comparison != 0)
                    return comparator.IsAscending ? comparison : -comparison;
            }
            return string.CompareOrdinal(left.Id, right.Id);
        });
        return cards.Order(comparer).ToList();
    }

    private static bool TryBuildPredicate(
        JsonNode? filter,
        out Func<JmapContactCardView, bool> predicate,
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
            var children = new List<Func<JmapContactCardView, bool>>();
            foreach (var condition in conditions)
            {
                if (!TryBuildPredicate(condition, out var child, out error))
                    return false;
                children.Add(child);
            }
            predicate = operation switch
            {
                "AND" => card => children.All(child => child(card)),
                "OR" => card => children.Any(child => child(card)),
                _ => card => children.All(child => !child(card)),
            };
            return true;
        }
        if (value.Any(item => !FilterProperties.Contains(item.Key)))
        {
            error = "unsupportedFilter";
            return false;
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset? createdBefore = null;
        DateTimeOffset? createdAfter = null;
        DateTimeOffset? updatedBefore = null;
        DateTimeOffset? updatedAfter = null;
        foreach (var item in value)
        {
            if (item.Key is "createdBefore" or "createdAfter" or "updatedBefore" or "updatedAfter")
            {
                if (item.Value is not JsonValue dateValue
                    || !dateValue.TryGetValue<string>(out var dateText)
                    || !JmapDate.TryParseUtcDate(dateText, out var date))
                {
                    error = "invalidArguments";
                    return false;
                }
                switch (item.Key)
                {
                    case "createdBefore": createdBefore = date; break;
                    case "createdAfter": createdAfter = date; break;
                    case "updatedBefore": updatedBefore = date; break;
                    case "updatedAfter": updatedAfter = date; break;
                }
            }
            else if (item.Value is not JsonValue stringValue
                || !stringValue.TryGetValue<string>(out var text)
                || text is null
                || item.Key == "inAddressBook" && !JmapId.IsValidId(text))
            {
                error = "invalidArguments";
                return false;
            }
            else
            {
                strings[item.Key] = text;
            }
        }

        predicate = view =>
            (!strings.TryGetValue("inAddressBook", out var addressBook)
                || string.Equals(view.AddressBookId, addressBook, StringComparison.Ordinal))
            && (!strings.TryGetValue("uid", out var uid)
                || string.Equals(StringValue(view.Card["uid"]), uid, StringComparison.Ordinal))
            && (!strings.TryGetValue("kind", out var kind)
                || string.Equals(StringValue(view.Card["kind"]) ?? "individual", kind, StringComparison.Ordinal))
            && (!strings.TryGetValue("hasMember", out var member)
                || view.Card["members"] is JsonObject members && members.ContainsKey(member))
            && (createdBefore is null || CardDate(view, "created", view.Resource.CreatedAt) < createdBefore)
            && (createdAfter is null || CardDate(view, "created", view.Resource.CreatedAt) >= createdAfter)
            && (updatedBefore is null || CardDate(view, "updated", view.Resource.UpdatedAt) < updatedBefore)
            && (updatedAfter is null || CardDate(view, "updated", view.Resource.UpdatedAt) >= updatedAfter)
            && strings.Where(item => item.Key is not (
                    "inAddressBook" or "uid" or "kind" or "hasMember"))
                .All(item => MatchesField(view.Card, item.Key, item.Value));
        return true;
    }

    private static bool MatchesField(JsonObject card, string property, string query)
    {
        IEnumerable<string> values = property switch
        {
            "text" => DescendantStrings(card),
            "name" => NameStrings(card),
            "name/given" => [NameComponent(card, "given")],
            "name/surname" => [NameComponent(card, "surname")],
            "name/surname2" => [NameComponent(card, "surname2")],
            "nickname" => MapStrings(card["nicknames"], "name"),
            "organization" => MapStrings(card["organizations"], "name"),
            "email" => MapStrings(card["emails"], "address", "label"),
            "phone" => MapStrings(card["phones"], "number", "label"),
            "onlineService" => MapStrings(card["onlineServices"], "service", "uri", "user", "label"),
            "address" => AddressStrings(card),
            "note" => MapStrings(card["notes"], "note"),
            _ => [],
        };
        var haystacks = values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        return ParseSearchTerms(query).All(term =>
            haystacks.Any(value => value.Contains(term, StringComparison.InvariantCultureIgnoreCase)));
    }

    private static IEnumerable<string> ParseSearchTerms(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (escaped)
            current.Append('\\');
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static IEnumerable<string> DescendantStrings(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return [text];
        if (node is JsonObject obj)
            return obj.SelectMany(item => DescendantStrings(item.Value));
        if (node is JsonArray array)
            return array.SelectMany(DescendantStrings);
        return [];
    }

    private static IEnumerable<string> NameStrings(JsonObject card)
    {
        if (card["name"] is not JsonObject name)
            return [];
        var result = new List<string>();
        if (StringValue(name["full"]) is { } full)
            result.Add(full);
        if (name["components"] is JsonArray components)
        {
            result.AddRange(components.OfType<JsonObject>()
                .Select(component => StringValue(component["value"]))
                .Where(value => value is not null)!);
        }
        return result;
    }

    private static IEnumerable<string> AddressStrings(JsonObject card) =>
        MapObjects(card["addresses"]).SelectMany(address =>
            (StringValue(address["full"]) is { } full ? new[] { full } : [])
            .Concat(address["components"] is JsonArray components
                ? components.OfType<JsonObject>()
                    .Select(component => StringValue(component["value"]))
                    .OfType<string>()
                : []));

    private static IEnumerable<string> MapStrings(JsonNode? node, params string[] properties) =>
        MapObjects(node).SelectMany(value => properties
            .Select(property => StringValue(value[property]))
            .OfType<string>());

    private static IEnumerable<JsonObject> MapObjects(JsonNode? node) =>
        node is JsonObject map ? map.Select(item => item.Value).OfType<JsonObject>() : [];

    private static string NameComponent(JsonObject card, string kind)
    {
        if (card["name"]?["components"] is not JsonArray components)
            return string.Empty;
        return components.OfType<JsonObject>()
            .Where(component => string.Equals(
                StringValue(component["kind"]), kind, StringComparison.Ordinal))
            .Select(component => StringValue(component["value"]))
            .FirstOrDefault(value => value is not null) ?? string.Empty;
    }

    private static DateTimeOffset CardDate(
        JmapContactCardView view,
        string property,
        DateTime fallback) =>
        StringValue(view.Card[property]) is { } text
        && JmapDate.TryParseUtcDate(text, out var parsed)
            ? parsed
            : new DateTimeOffset(DateTime.SpecifyKind(fallback, DateTimeKind.Utc));

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

internal sealed class ContactCardQueryMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "ContactCard/query";
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments, "accountId", "filter", "sort", "position", "anchor",
                "anchorOffset", "limit", "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetQueryWindow(arguments, out var position, out var anchor, out var anchorOffset)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "limit", out var requestedLimit)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken);
        var cards = await contacts.LoadCardsAsync(account.UserId, false, cancellationToken);
        if (!JmapContactQueryEngine.TryFilter(cards, arguments["filter"], out var filtered, out var filterError))
            return JmapMethodResponse.Error(filterError);
        if (!JmapContactQueryEngine.TryParseSort(arguments["sort"], out var comparators, out var sortError))
            return JmapMethodResponse.Error(sortError);
        var ids = JmapContactQueryEngine.Sort(filtered, comparators).Select(card => card.Id).ToList();
        if (anchor is not null)
        {
            var index = ids.IndexOf(anchor);
            if (index < 0)
                return JmapMethodResponse.Error("anchorNotFound");
            position = Math.Min(JmapMethodHelpers.MaximumInt, Math.Max(0L, index + anchorOffset));
        }
        else if (position < 0)
        {
            position = Math.Max(0L, ids.Count + position);
        }
        var limit = JmapMethodHelpers.ClampToServerLimit(requestedLimit, environment.Jmap.MaxObjectsInGet);
        var pagePosition = position >= ids.Count ? ids.Count : checked((int)position);
        var page = ids.Skip(pagePosition).Take(limit).ToArray();
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["queryState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.ContactCardDataType,
                cancellationToken),
            ["canCalculateChanges"] = true,
            ["position"] = position,
            ["ids"] = JmapMethodHelpers.ToJsonArray(page),
        };
        if (calculateTotal) response["total"] = ids.Count;
        if (requestedLimit is null || requestedLimit > limit) response["limit"] = limit;
        return new JmapMethodResponse(Name, response);
    }
}

internal sealed class ContactCardQueryChangesMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states) : IJmapMethod
{
    public string Name => "ContactCard/queryChanges";
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments, "accountId", "filter", "sort", "sinceQueryState",
                "maxChanges", "upToId", "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || !JmapMethodHelpers.TryGetOptionalId(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken);
        var cards = await contacts.LoadCardsAsync(account.UserId, false, cancellationToken);
        if (!JmapContactQueryEngine.TryFilter(cards, arguments["filter"], out var filtered, out var filterError))
            return JmapMethodResponse.Error(filterError);
        if (!JmapContactQueryEngine.TryParseSort(arguments["sort"], out var comparators, out var sortError))
            return JmapMethodResponse.Error(sortError);
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.ContactCardDataType,
            sinceState,
            null,
            int.MaxValue,
            cancellationToken);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");
        var currentIds = JmapContactQueryEngine.Sort(filtered, comparators)
            .Select(card => card.Id).ToList();
        var currentSet = currentIds.ToHashSet(StringComparer.Ordinal);
        var mutableQuery = comparators.Count > 0
            || JmapContactQueryEngine.IsFilterMutable(arguments["filter"]);
        var immutableQueryMatchesAll = !mutableQuery
            && JmapContactQueryEngine.ImmutableFilterMatchesAll(arguments["filter"]);
        IEnumerable<string> removedChanges = mutableQuery
            ? changes.Destroyed.Concat(changes.Updated)
            : immutableQueryMatchesAll ? changes.Destroyed : [];
        IEnumerable<string> addedChanges = mutableQuery
            ? changes.Created.Concat(changes.Updated)
            : immutableQueryMatchesAll ? changes.Created : [];
        var removed = removedChanges.Distinct(StringComparer.Ordinal).ToArray();
        var changedCurrent = addedChanges
            .Where(currentSet.Contains).ToHashSet(StringComparer.Ordinal);
        var added = currentIds.Select((id, index) => new { id, index })
            .Where(item => changedCurrent.Contains(item.id)).ToArray();
        if (maxChanges is not null && removed.LongLength + added.LongLength > maxChanges)
            return JmapMethodResponse.Error("tooManyChanges");
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldQueryState"] = sinceState,
            ["newQueryState"] = changes.NewState,
            ["removed"] = JmapMethodHelpers.ToJsonArray(removed),
            ["added"] = new JsonArray(added.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.id,
                ["index"] = item.index,
            }).ToArray()),
        };
        if (calculateTotal) response["total"] = filtered.Count;
        return new JmapMethodResponse(Name, response);
    }
}
