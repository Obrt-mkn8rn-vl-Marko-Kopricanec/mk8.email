using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static partial class JmapEmailQueryEngine
{
    private static readonly HashSet<string> ConditionProperties = new HashSet<string>(
        [
            "inMailbox", "inMailboxOtherThan", "before", "after", "minSize", "maxSize",
            "allInThreadHaveKeyword", "someInThreadHaveKeyword", "noneInThreadHaveKeyword",
            "hasKeyword", "notKeyword", "hasAttachment", "text", "from", "to", "cc",
            "bcc", "subject", "body", "header",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SortProperties = new HashSet<string>(
        [
            "receivedAt", "size", "from", "to", "subject", "sentAt", "hasKeyword",
            "allInThreadHaveKeyword", "someInThreadHaveKeyword",
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> MutableFilterProperties = new HashSet<string>(
        [
            "inMailbox", "inMailboxOtherThan", "allInThreadHaveKeyword",
            "someInThreadHaveKeyword", "noneInThreadHaveKeyword", "hasKeyword",
            "notKeyword",
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> ThreadFilterProperties = new HashSet<string>(
        ["allInThreadHaveKeyword", "someInThreadHaveKeyword", "noneInThreadHaveKeyword"],
        StringComparer.Ordinal);

    public static async Task<List<JmapEmailQueryItem>> LoadAsync(
        EmailDbContext database,
        MailboxMessageContentService content,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var emails = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.InboxId == accountId && !email.IsDeleted)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<JmapEmailQueryItem>(emails.Count);
        try
        {
            for (var emailIndex = 0; emailIndex < emails.Count; emailIndex++)
            {
                var email = emails[emailIndex];
                var rawMessage = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                var message = JmapEmailCodec.Parse(rawMessage);
                var keywords = JmapEmailCodec.BuildKeywords(email)
                    .Select(item => item.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                result.Add(new JmapEmailQueryItem(
                    email,
                    message,
                    email.SizeBytes > 0 ? email.SizeBytes : rawMessage.LongLength,
                    keywords,
                    JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
                    JmapEmailCodec.HasAttachment(message),
                    JmapEmailCodec.FirstAddressInLastHeader(message, "From"),
                    JmapEmailCodec.FirstAddressInLastHeader(message, "To"),
                    JmapEmailCodec.LastTextHeader(message, "Subject"),
                    JmapEmailCodec.LastDateHeader(message, "Date")));
            }
            return result;
        }
        catch
        {
            for (var itemIndex = 0; itemIndex < result.Count; itemIndex++)
                result[itemIndex].Dispose();
            throw;
        }
    }

    public static bool TryFilter(
        IReadOnlyList<JmapEmailQueryItem> items,
        JsonNode? filter,
        out List<JmapEmailQueryItem> result,
        out string error)
    {
        result = [];
        if (!TryBuildPredicate(items, filter, out var predicate, out error))
            return false;
        result = items.Where(predicate).ToList();
        return true;
    }

    public static bool TryParseSort(
        JsonNode? node,
        out IReadOnlyList<JmapEmailComparator> result,
        out string error)
    {
        error = string.Empty;
        if (node is null)
        {
            result = [new JmapEmailComparator("receivedAt", false, null, null)];
            return true;
        }
        if (node is not JsonArray array)
        {
            result = [];
            error = "invalidArguments";
            return false;
        }

        var comparators = new List<JmapEmailComparator>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject comparator
                || !JmapMethodHelpers.HasOnlyProperties(
                    comparator,
                    "property",
                    "isAscending",
                    "keyword",
                    "collation")
                || !JmapMethodHelpers.TryGetRequiredString(comparator, "property", out var property)
                || !JmapMethodHelpers.TryGetOptionalBoolean(comparator, "isAscending", true, out var ascending)
                || !JmapMethodHelpers.TryGetOptionalString(
                    comparator,
                    "keyword",
                    out var keyword,
                    allowNull: false)
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
            if (!SortProperties.Contains(property))
            {
                result = [];
                error = "unsupportedSort";
                return false;
            }
            var needsKeyword = property is
                "hasKeyword" or "allInThreadHaveKeyword" or "someInThreadHaveKeyword";
            if (needsKeyword != (keyword is not null)
                || keyword is not null && !JmapEmailCodec.IsValidKeyword(keyword.ToLowerInvariant()))
            {
                result = [];
                error = "invalidArguments";
                return false;
            }
            var comparesStrings = property is "from" or "to" or "subject";
            if (comparesStrings
                && collation is not null
                && !JmapCollation.IsSupported(collation))
            {
                result = [];
                error = "unsupportedSort";
                return false;
            }
            comparators.Add(new JmapEmailComparator(property, ascending, keyword?.ToLowerInvariant(), collation));
        }
        result = comparators;
        return true;
    }

    public static List<JmapEmailQueryItem> Sort(
        IReadOnlyList<JmapEmailQueryItem> all,
        IReadOnlyList<JmapEmailQueryItem> filtered,
        IReadOnlyList<JmapEmailComparator> comparators)
    {
        var byThread = all.ToLookup(item => item.ThreadId, StringComparer.Ordinal);
        var comparer = Comparer<JmapEmailQueryItem>.Create((left, right) =>
        {
            foreach (var comparator in comparators)
            {
                var comparison = Compare(left, right, comparator, byThread);
                if (comparison != 0)
                    return comparator.IsAscending ? comparison : -comparison;
            }
            return left.Email.Id.CompareTo(right.Email.Id);
        });
        return filtered.Order(comparer).ToList();
    }

    public static bool UsesMutableFilter(JsonNode? filter) =>
        FilterUsesAnyProperty(filter, MutableFilterProperties);

    public static bool UsesThreadProperties(
        JsonNode? filter,
        IReadOnlyList<JmapEmailComparator> comparators) =>
        FilterUsesAnyProperty(filter, ThreadFilterProperties)
        || comparators.Any(comparator => comparator.Property is
            "allInThreadHaveKeyword" or "someInThreadHaveKeyword");

    public static bool UsesMutableSort(IReadOnlyList<JmapEmailComparator> comparators) =>
        comparators.Any(comparator => comparator.Property is
            "hasKeyword" or "allInThreadHaveKeyword" or "someInThreadHaveKeyword");

    private static bool FilterUsesAnyProperty(
        JsonNode? node,
        IReadOnlySet<string> properties)
    {
        if (node is not JsonObject value)
            return false;
        if (value["conditions"] is JsonArray conditions)
            return conditions.Any(condition => FilterUsesAnyProperty(condition, properties));
        return value.Any(item => properties.Contains(item.Key));
    }

    private static bool TryBuildPredicate(
        IReadOnlyList<JmapEmailQueryItem> all,
        JsonNode? node,
        out Func<JmapEmailQueryItem, bool> predicate,
        out string error)
    {
        predicate = static _ => true;
        error = string.Empty;
        if (node is null)
            return true;
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
            var children = new List<Func<JmapEmailQueryItem, bool>>(conditions.Count);
            foreach (var condition in conditions)
            {
                if (!TryBuildPredicate(all, condition, out var child, out error))
                    return false;
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

        var unknown = value.Select(item => item.Key)
            .FirstOrDefault(property => !ConditionProperties.Contains(property));
        if (unknown is not null)
        {
            error = "unsupportedFilter";
            return false;
        }

        string? inMailbox = null;
        if (value.TryGetPropertyValue("inMailbox", out var mailboxNode))
        {
            if (!TryMailboxId(mailboxNode, out var parsedMailbox))
            {
                error = "invalidArguments";
                return false;
            }
            inMailbox = parsedMailbox;
        }
        HashSet<string>? otherThan = null;
        if (value.TryGetPropertyValue("inMailboxOtherThan", out var otherNode))
        {
            if (otherNode is not JsonArray otherArray)
            {
                error = "invalidArguments";
                return false;
            }
            otherThan = [];
            foreach (var item in otherArray)
            {
                if (!TryMailboxId(item, out var parsedMailbox))
                {
                    error = "invalidArguments";
                    return false;
                }
                otherThan.Add(parsedMailbox);
            }
        }

        if (!TryUtcDate(value, "before", out var before)
            || !TryUtcDate(value, "after", out var after)
            || !TryUnsignedLong(value, "minSize", out var minimumSize)
            || !TryUnsignedLong(value, "maxSize", out var maximumSize)
            || !TryKeyword(value, "allInThreadHaveKeyword", out var allThreadKeyword)
            || !TryKeyword(value, "someInThreadHaveKeyword", out var someThreadKeyword)
            || !TryKeyword(value, "noneInThreadHaveKeyword", out var noThreadKeyword)
            || !TryKeyword(value, "hasKeyword", out var hasKeyword)
            || !TryKeyword(value, "notKeyword", out var notKeyword)
            || !TryOptionalBool(value, "hasAttachment", out var hasAttachment)
            || !TryOptionalText(value, "text", out var text)
            || !TryOptionalText(value, "from", out var from)
            || !TryOptionalText(value, "to", out var to)
            || !TryOptionalText(value, "cc", out var cc)
            || !TryOptionalText(value, "bcc", out var bcc)
            || !TryOptionalText(value, "subject", out var subject)
            || !TryOptionalText(value, "body", out var body)
            || !TryHeaderFilter(value, out var headerName, out var headerText))
        {
            error = "invalidArguments";
            return false;
        }

        var byThread = all.ToLookup(item => item.ThreadId, StringComparer.Ordinal);
        predicate = item =>
            (inMailbox is null
                || string.Equals(
                    JmapId.Mailbox(item.Email.FolderId),
                    inMailbox,
                    StringComparison.Ordinal))
            && (otherThan is null
                || !otherThan.Contains(JmapId.Mailbox(item.Email.FolderId)))
            && (before is null || item.Email.ReceivedAt.ToUniversalTime() < before.Value.UtcDateTime)
            && (after is null || item.Email.ReceivedAt.ToUniversalTime() >= after.Value.UtcDateTime)
            && (minimumSize is null || item.Size >= minimumSize)
            && (maximumSize is null || item.Size < maximumSize)
            && (allThreadKeyword is null || byThread[item.ThreadId].All(threadItem => threadItem.Keywords.Contains(allThreadKeyword)))
            && (someThreadKeyword is null || byThread[item.ThreadId].Any(threadItem => threadItem.Keywords.Contains(someThreadKeyword)))
            && (noThreadKeyword is null || byThread[item.ThreadId].All(threadItem => !threadItem.Keywords.Contains(noThreadKeyword)))
            && (hasKeyword is null || item.Keywords.Contains(hasKeyword))
            && (notKeyword is null || !item.Keywords.Contains(notKeyword))
            && (hasAttachment is null || item.HasAttachment == hasAttachment)
            && (text is null || MatchesText(AllSearchableText(item), text))
            && (from is null || MatchesText(HeaderText(item.Message, "From"), from))
            && (to is null || MatchesText(HeaderText(item.Message, "To"), to))
            && (cc is null || MatchesText(HeaderText(item.Message, "Cc"), cc))
            && (bcc is null || MatchesText(HeaderText(item.Message, "Bcc"), bcc))
            && (subject is null || MatchesText(HeaderText(item.Message, "Subject"), subject))
            && (body is null || MatchesText(BodyText(item), body))
            && (headerName is null || MatchesHeader(item.Message, headerName, headerText));
        return true;
    }

    private static int Compare(
        JmapEmailQueryItem left,
        JmapEmailQueryItem right,
        JmapEmailComparator comparator,
        ILookup<string, JmapEmailQueryItem> byThread)
    {
        return comparator.Property switch
        {
            "receivedAt" => left.Email.ReceivedAt.CompareTo(right.Email.ReceivedAt),
            "size" => left.Size.CompareTo(right.Size),
            "from" => CompareString(left.FromSortValue, right.FromSortValue, comparator.Collation),
            "to" => CompareString(left.ToSortValue, right.ToSortValue, comparator.Collation),
            "subject" => CompareString(BaseSubject(left.SubjectSortValue), BaseSubject(right.SubjectSortValue), comparator.Collation),
            "sentAt" => Nullable.Compare(left.SentAtSortValue, right.SentAtSortValue),
            "hasKeyword" => left.Keywords.Contains(comparator.Keyword!).CompareTo(right.Keywords.Contains(comparator.Keyword!)),
            "allInThreadHaveKeyword" => byThread[left.ThreadId].All(item => item.Keywords.Contains(comparator.Keyword!))
                .CompareTo(byThread[right.ThreadId].All(item => item.Keywords.Contains(comparator.Keyword!))),
            "someInThreadHaveKeyword" => byThread[left.ThreadId].Any(item => item.Keywords.Contains(comparator.Keyword!))
                .CompareTo(byThread[right.ThreadId].Any(item => item.Keywords.Contains(comparator.Keyword!))),
            _ => 0,
        };
    }

    private static string AllSearchableText(JmapEmailQueryItem item) => string.Join(
        '\n',
        HeaderText(item.Message, "From"),
        HeaderText(item.Message, "To"),
        HeaderText(item.Message, "Cc"),
        HeaderText(item.Message, "Bcc"),
        HeaderText(item.Message, "Subject"),
        BodyText(item));

    private static string HeaderText(MimeMessage message, string name) =>
        JmapEmailCodec.SearchableHeaderText(message, name);

    private static string BodyText(JmapEmailQueryItem item) =>
        JmapEmailCodec.SearchableBodyText(item.Message);

    private static bool MatchesText(string haystack, string query)
    {
        foreach (var token in SearchTokenRegex().Matches(query).Select(match => match.Value))
        {
            var normalized = token.Length >= 2 && token[0] is '\'' or '"' && token[^1] == token[0]
                ? token[1..^1]
                : token;
            normalized = normalized.Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            if (normalized.Length > 0
                && !haystack.Contains(normalized, StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesHeader(MimeMessage message, string name, string? text)
    {
        var headers = JmapEmailCodec.MessageHeaders(message)
            .Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return headers.Length > 0
            && (text is null || headers.Any(header => MatchesText(header.Value, text)));
    }

    private static bool TryMailboxId(JsonNode? node, out string id)
    {
        id = string.Empty;
        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var stringValue)
            || stringValue is null
            || !JmapId.IsValidId(stringValue))
        {
            return false;
        }
        id = stringValue;
        return true;
    }

    private static bool TryUtcDate(JsonObject value, string name, out DateTimeOffset? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node))
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text is null
            || !JmapDate.TryParseUtcDate(text, out var parsed))
        {
            return false;
        }
        result = parsed;
        return true;
    }

    private static bool TryUnsignedLong(JsonObject value, string name, out long? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node))
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<long>(out var parsed)
            || parsed is < 0 or > JmapMethodHelpers.MaximumInt)
        {
            return false;
        }
        result = parsed;
        return true;
    }

    private static bool TryKeyword(JsonObject value, string name, out string? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node))
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out result)
            || result is null)
        {
            return false;
        }
        result = result.ToLowerInvariant();
        return JmapEmailCodec.IsValidKeyword(result);
    }

    private static bool TryOptionalBool(JsonObject value, string name, out bool? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node))
            return true;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<bool>(out var parsed))
            return false;
        result = parsed;
        return true;
    }

    private static bool TryOptionalText(JsonObject value, string name, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(name, out var node)
            || node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out result)
            && result is not null;
    }

    private static bool TryHeaderFilter(
        JsonObject value,
        out string? name,
        out string? text)
    {
        name = null;
        text = null;
        if (!value.TryGetPropertyValue("header", out var node))
            return true;
        if (node is not JsonArray { Count: 1 or 2 } array
            || array[0] is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out name)
            || string.IsNullOrEmpty(name)
            || name.Any(character => character is < (char)33 or > (char)126 || character == ':'))
        {
            return false;
        }
        return array.Count == 1
            || array[1] is JsonValue textValue
            && textValue.TryGetValue<string>(out text)
            && text is not null;
    }

    internal static string BaseSubject(string? value) => Rfc5256.BaseSubject(value);

    private static int CompareString(string left, string right, string? collation) =>
        JmapCollation.Compare(left, right, collation);

    [GeneratedRegex("(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'(?:\\\\.|[^'])*'|\\S+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SearchTokenRegex();

}
