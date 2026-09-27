using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal static class JmapContactQueryEngine
{
    private static readonly HashSet<string> FilterProperties = new HashSet<string>(
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

    private static List<string> ParseSearchTerms(string value)
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

    private static List<string> NameStrings(JsonObject card)
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
