using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal static partial class JmapSearchSnippetFormatter
{
    public static IReadOnlyList<string> ExtractTerms(JsonNode? filter)
    {
        var result = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        Visit(filter, negated: false);
        return result.OrderByDescending(term => term.Length).ToArray();

        void Visit(JsonNode? node, bool negated)
        {
            if (node is not JsonObject value)
                return;
            var childNegated = negated;
            if (value["operator"] is JsonValue operatorValue
                && operatorValue.TryGetValue<string>(out var operation)
                && operation == "NOT")
            {
                childNegated = !childNegated;
            }
            if (value["conditions"] is JsonArray conditions)
            {
                foreach (var condition in conditions)
                    Visit(condition, childNegated);
            }
            if (negated)
                return;
            foreach (var name in new[] { "text", "from", "to", "cc", "bcc", "subject", "body" })
            {
                if (value[name] is JsonValue textValue
                    && textValue.TryGetValue<string>(out var text))
                {
                    AddTerms(text);
                }
            }
            if (value["header"] is JsonArray { Count: 2 } header
                && header[1] is JsonValue headerValue
                && headerValue.TryGetValue<string>(out var headerText))
            {
                AddTerms(headerText);
            }
        }

        void AddTerms(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            foreach (Match match in TermRegex().Matches(value))
            {
                var term = match.Value.Trim('"', '\'')
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\'", "'", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
                if (term.Length > 0)
                    result.Add(term);
            }
        }
    }

    public static string? HighlightSubject(string? value, IReadOnlyList<string> terms) =>
        string.IsNullOrEmpty(value) || !ContainsAny(value, terms)
            ? null
            : Highlight(value, terms);

    public static string? HighlightPreview(string value, IReadOnlyList<string> terms)
    {
        var term = terms.FirstOrDefault(candidate =>
            value.Contains(candidate, StringComparison.InvariantCultureIgnoreCase));
        if (term is null)
            return null;
        var index = value.IndexOf(term, StringComparison.InvariantCultureIgnoreCase);
        var start = Math.Max(0, index - 80);
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start]))
            start++;
        var end = Math.Min(value.Length, start + 180);
        if (end > start
            && end < value.Length
            && char.IsHighSurrogate(value[end - 1])
            && char.IsLowSurrogate(value[end]))
        {
            end--;
        }
        var snippet = WhiteSpaceRegex().Replace(value[start..end], " ").Trim();
        if (start > 0) snippet = "…" + snippet;
        if (end < value.Length) snippet += "…";
        var highlighted = Highlight(snippet, terms);
        while (Encoding.UTF8.GetByteCount(highlighted) > 255)
        {
            var matchIndex = snippet.IndexOf(
                term,
                StringComparison.InvariantCultureIgnoreCase);
            if (matchIndex < 0)
                return null;
            if (matchIndex + term.Length < snippet.Length)
                snippet = RemoveLastRune(snippet);
            else if (matchIndex > 0)
                snippet = RemoveFirstRune(snippet);
            else
                return null;
            highlighted = Highlight(snippet, terms);
        }
        return highlighted;
    }

    private static string RemoveFirstRune(string value)
    {
        var length = value.Length > 1 && char.IsSurrogatePair(value[0], value[1]) ? 2 : 1;
        return value[length..];
    }

    private static string RemoveLastRune(string value)
    {
        var length = value.Length > 1
            && char.IsSurrogatePair(value[^2], value[^1])
                ? 2
                : 1;
        return value[..^length];
    }

    public static string PlainBody(MimeKit.MimeMessage message) =>
        JmapEmailCodec.SearchableBodyText(message);

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.InvariantCultureIgnoreCase));

    private static string Highlight(string value, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return WebUtility.HtmlEncode(value);
        var pattern = string.Join('|', terms.Select(Regex.Escape));
        var expression = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var builder = new StringBuilder();
        var previous = 0;
        foreach (Match match in expression.Matches(value))
        {
            builder.Append(WebUtility.HtmlEncode(value[previous..match.Index]));
            builder.Append("<mark>")
                .Append(WebUtility.HtmlEncode(match.Value))
                .Append("</mark>");
            previous = match.Index + match.Length;
        }
        builder.Append(WebUtility.HtmlEncode(value[previous..]));
        return builder.ToString();
    }

    [GeneratedRegex("(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'(?:\\\\.|[^'])*'|\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex TermRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRegex();

}

internal sealed class SearchSnippetGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    MailboxMessageContentService content,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "SearchSnippet/get";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "filter", "emailIds")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapEmailArguments.TryGetIds(arguments, "emailIds", false, out var emailIds)
            || emailIds is null)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (emailIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");

        var all = await JmapEmailQueryEngine.LoadAsync(
            database,
            content,
            account.InboxId,
            cancellationToken);
        try
        {
            if (!JmapEmailQueryEngine.TryFilter(
                    all,
                    arguments["filter"],
                    out _,
                    out var filterError))
            {
                return JmapMethodResponse.Error(filterError == "invalidArguments"
                    ? "unsupportedFilter"
                    : filterError);
            }
            var byId = all.ToDictionary(item => JmapId.Email(item.Email.Id), StringComparer.Ordinal);
            var terms = JmapSearchSnippetFormatter.ExtractTerms(arguments["filter"]);
            var list = new JsonArray();
            var notFound = new JsonArray();
            foreach (var id in emailIds.Distinct(StringComparer.Ordinal))
            {
                if (!byId.TryGetValue(id, out var item))
                {
                    notFound.Add(id);
                    continue;
                }
                list.Add(new JsonObject
                {
                    ["emailId"] = id,
                    ["subject"] = JmapSearchSnippetFormatter.HighlightSubject(
                        JmapEmailCodec.LastTextHeader(item.Message, "Subject"),
                        terms),
                    ["preview"] = JmapSearchSnippetFormatter.HighlightPreview(
                        JmapSearchSnippetFormatter.PlainBody(item.Message),
                        terms),
                });
            }
            return new JmapMethodResponse(Name, new JsonObject
            {
                ["accountId"] = accountId,
                ["list"] = list,
                ["notFound"] = notFound.Count == 0 ? null : notFound,
            });
        }
        finally
        {
            foreach (var item in all)
                item.Dispose();
        }
    }
}
