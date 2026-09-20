using MimeKit;
using MimeKit.Utils;

namespace mk8.email.Jmap;

internal static class JmapMessageId
{
    public static bool TryParseParsedForm(string value, out string parsed)
    {
        parsed = string.Empty;
        if (string.IsNullOrEmpty(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.IndexOfAny(['<', '>', '\r', '\n']) >= 0)
        {
            return false;
        }

        try
        {
            var candidate = MimeUtils.ParseMessageId($"<{value}>");
            var separator = candidate?.LastIndexOf('@') ?? -1;
            if (candidate is null
                || !string.Equals(candidate, value, StringComparison.Ordinal)
                || separator <= 0
                || separator == candidate.Length - 1)
            {
                return false;
            }

            parsed = candidate;
            return true;
        }
        catch (ParseException)
        {
            return false;
        }
    }
}
