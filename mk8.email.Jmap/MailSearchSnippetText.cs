using System.Text.RegularExpressions;

namespace mk8.email.Jmap;

internal static partial class MailSearchSnippetText
{
    public static string? SelectSubject(string? value, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var term = terms.FirstOrDefault(candidate =>
            value.Contains(candidate, StringComparison.InvariantCultureIgnoreCase));
        if (term is null) return null;
        if (value.Length <= 4096) return value;
        var index = value.IndexOf(term, StringComparison.InvariantCultureIgnoreCase);
        return SelectWindow(value, index, 2048, 4096);
    }

    public static string? SelectPreview(string value, IReadOnlyList<string> terms)
    {
        var term = terms.FirstOrDefault(candidate =>
            value.Contains(candidate, StringComparison.InvariantCultureIgnoreCase));
        if (term is null) return null;
        var index = value.IndexOf(term, StringComparison.InvariantCultureIgnoreCase);
        return SelectWindow(value, index, 80, 180);
    }

    private static string SelectWindow(string value, int index, int context, int length)
    {
        var start = Math.Max(0, index - context);
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start])) start++;
        var end = Math.Min(value.Length, start + length);
        if (end > start && end < value.Length && char.IsHighSurrogate(value[end - 1])
            && char.IsLowSurrogate(value[end])) end--;
        var selected = WhiteSpaceRegex().Replace(value[start..end], " ").Trim();
        if (start > 0) selected = "…" + selected;
        if (end < value.Length) selected += "…";
        return selected;
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WhiteSpaceRegex();
}
