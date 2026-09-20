using System.Net;
using System.Text.RegularExpressions;

namespace mk8.email.Jmap;

internal static partial class JmapHtmlText
{
    public static string Extract(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var rendered = NonRenderedContentRegex().Replace(html, " ");
        rendered = HtmlTagRegex().Replace(rendered, " ");
        return WebUtility.HtmlDecode(rendered);
    }

    [GeneratedRegex(
        "<!--.*?-->|<(?:head|script|style)\\b[^>]*>.*?</(?:head|script|style)\\s*>",
        RegexOptions.IgnoreCase
        | RegexOptions.Singleline
        | RegexOptions.CultureInvariant
        | RegexOptions.NonBacktracking)]
    private static partial Regex NonRenderedContentRegex();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTagRegex();
}
