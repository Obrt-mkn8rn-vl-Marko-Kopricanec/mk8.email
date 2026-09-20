namespace mk8.email.Jmap;

internal static class JmapLanguageTag
{
    public static bool IsValid(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var subtags = value.Split('-');
        if (subtags.Length == 0
            || subtags[0].Length is < 1 or > 8
            || !subtags[0].All(char.IsAsciiLetter))
        {
            return false;
        }

        return subtags.Skip(1).All(subtag =>
            subtag.Length is >= 1 and <= 8
            && subtag.All(char.IsAsciiLetterOrDigit));
    }

    public static bool TryParseHeader(string value, out IReadOnlyList<string> languages)
    {
        languages = [];
        var parsed = new List<string>();
        var index = 0;
        if (!TrySkipCfws(value, ref index) || index == value.Length)
            return false;

        while (index < value.Length)
        {
            var start = index;
            while (index < value.Length
                && (char.IsAsciiLetterOrDigit(value[index]) || value[index] == '-'))
            {
                index++;
            }

            var language = value[start..index];
            if (!IsValid(language) || !TrySkipCfws(value, ref index))
                return false;
            parsed.Add(language);

            if (index == value.Length)
                break;
            if (value[index] != ',')
                return false;
            index++;
            if (!TrySkipCfws(value, ref index) || index == value.Length)
                return false;
        }

        languages = parsed;
        return parsed.Count > 0;
    }

    private static bool TrySkipCfws(string value, ref int index)
    {
        while (index < value.Length)
        {
            if (value[index] is ' ' or '\t' or '\r' or '\n')
            {
                index++;
                continue;
            }
            if (value[index] != '(')
                return true;

            var depth = 1;
            index++;
            while (index < value.Length && depth > 0)
            {
                switch (value[index++])
                {
                    case '\\':
                        if (index == value.Length)
                            return false;
                        index++;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                }
            }
            if (depth != 0)
                return false;
        }
        return true;
    }
}
