using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class JmapContactQueryEngine
{
    public static List<JmapContactCardView> Filter(
        IReadOnlyList<JmapContactCardView> cards,
        MailContactFilter? filter) => cards.Where(BuildPredicate(filter)).ToList();

    public static bool IsFilterMutable(MailContactFilter? filter)
    {
        if (filter is null) return false;
        return filter.Operator == MailContactFilterOperator.Condition
            ? filter.Terms is { Count: > 0 } || filter.CreatedBefore is not null
                || filter.CreatedAfter is not null || filter.UpdatedBefore is not null
                || filter.UpdatedAfter is not null
            : filter.Conditions!.Any(IsFilterMutable);
    }

    public static bool ImmutableFilterMatchesAll(MailContactFilter? filter)
    {
        if (filter is null || filter.Operator == MailContactFilterOperator.Condition)
            return true;
        var conditions = filter.Conditions!.Select(ImmutableFilterMatchesAll).ToArray();
        return filter.Operator switch
        {
            MailContactFilterOperator.And => conditions.All(result => result),
            MailContactFilterOperator.Or => conditions.Any(result => result),
            _ => conditions.All(result => !result),
        };
    }

    public static List<JmapContactCardView> Sort(
        IEnumerable<JmapContactCardView> cards,
        IReadOnlyList<MailContactSort> comparators)
    {
        var comparer = Comparer<JmapContactCardView>.Create((left, right) =>
        {
            foreach (var comparator in comparators)
            {
                var comparison = comparator.Field switch
                {
                    MailContactSortField.Created => CardDate(left, "created", left.Resource.CreatedAt)
                        .CompareTo(CardDate(right, "created", right.Resource.CreatedAt)),
                    MailContactSortField.Updated => CardDate(left, "updated", left.Resource.UpdatedAt)
                        .CompareTo(CardDate(right, "updated", right.Resource.UpdatedAt)),
                    _ => JmapCollation.Compare(
                        NameComponent(left.Card, NameKind(comparator.Field)),
                        NameComponent(right.Card, NameKind(comparator.Field)),
                        comparator.Collation),
                };
                if (comparison != 0)
                    return comparator.IsAscending ? comparison : -comparison;
            }
            return string.CompareOrdinal(left.Id, right.Id);
        });
        return cards.Order(comparer).ToList();
    }

    private static Func<JmapContactCardView, bool> BuildPredicate(MailContactFilter? filter)
    {
        if (filter is null)
            return static _ => true;
        if (filter.Operator != MailContactFilterOperator.Condition)
        {
            var children = filter.Conditions!.Select(BuildPredicate).ToArray();
            return filter.Operator switch
            {
                MailContactFilterOperator.And => card => children.All(child => child(card)),
                MailContactFilterOperator.Or => card => children.Any(child => child(card)),
                _ => card => children.All(child => !child(card)),
            };
        }
        return view =>
            (filter.CreatedBefore is null || CardDate(view, "created", view.Resource.CreatedAt) < filter.CreatedBefore)
            && (filter.CreatedAfter is null || CardDate(view, "created", view.Resource.CreatedAt) >= filter.CreatedAfter)
            && (filter.UpdatedBefore is null || CardDate(view, "updated", view.Resource.UpdatedAt) < filter.UpdatedBefore)
            && (filter.UpdatedAfter is null || CardDate(view, "updated", view.Resource.UpdatedAt) >= filter.UpdatedAfter)
            && filter.Terms!.All(term => MatchesTerm(view, term));
    }

    private static bool MatchesTerm(JmapContactCardView view, MailContactFilterTerm term) =>
        term.Field switch
        {
            MailContactFilterField.InAddressBook =>
                string.Equals(view.AddressBookId, term.Value, StringComparison.Ordinal),
            MailContactFilterField.Uid =>
                string.Equals(StringValue(view.Card["uid"]), term.Value, StringComparison.Ordinal),
            MailContactFilterField.Kind =>
                string.Equals(StringValue(view.Card["kind"]) ?? "individual", term.Value, StringComparison.Ordinal),
            MailContactFilterField.HasMember =>
                view.Card["members"] is JsonObject members && members.ContainsKey(term.Value),
            _ => MatchesField(view.Card, term.Field, term.Value),
        };

    private static bool MatchesField(JsonObject card, MailContactFilterField property, string query)
    {
        IEnumerable<string> values = property switch
        {
            MailContactFilterField.Text => DescendantStrings(card),
            MailContactFilterField.Name => NameStrings(card),
            MailContactFilterField.GivenName => [NameComponent(card, "given")],
            MailContactFilterField.Surname => [NameComponent(card, "surname")],
            MailContactFilterField.Surname2 => [NameComponent(card, "surname2")],
            MailContactFilterField.Nickname => MapStrings(card["nicknames"], "name"),
            MailContactFilterField.Organization => MapStrings(card["organizations"], "name"),
            MailContactFilterField.Email => MapStrings(card["emails"], "address", "label"),
            MailContactFilterField.Phone => MapStrings(card["phones"], "number", "label"),
            MailContactFilterField.OnlineService => MapStrings(card["onlineServices"], "service", "uri", "user", "label"),
            MailContactFilterField.Address => AddressStrings(card),
            MailContactFilterField.Note => MapStrings(card["notes"], "note"),
            _ => [],
        };
        var haystacks = values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        return ParseSearchTerms(query).All(term =>
            haystacks.Any(value => value.Contains(term, StringComparison.InvariantCultureIgnoreCase)));
    }

    private static string NameKind(MailContactSortField field) => field switch
    {
        MailContactSortField.GivenName => "given",
        MailContactSortField.Surname => "surname",
        _ => "surname2",
    };

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
