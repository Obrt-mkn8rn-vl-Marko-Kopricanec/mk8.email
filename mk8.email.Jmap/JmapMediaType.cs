namespace mk8.email.Jmap;

internal static class JmapMediaType
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
            return false;

        var separator = value.IndexOf('/');
        if (separator <= 0
            || separator != value.LastIndexOf('/')
            || !IsRestrictedName(value.AsSpan(0, separator))
            || !IsRestrictedName(value.AsSpan(separator + 1)))
        {
            return false;
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    private static bool IsRestrictedName(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 127 || !IsAsciiLetterOrDigit(value[0]))
            return false;

        foreach (var character in value[1..])
        {
            if (!IsAsciiLetterOrDigit(character)
                && character is not ('!' or '#' or '$' or '&' or '-' or '^' or '_' or '.' or '+'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';
}
