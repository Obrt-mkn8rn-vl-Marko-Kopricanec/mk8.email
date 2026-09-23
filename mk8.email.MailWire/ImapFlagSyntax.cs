namespace mk8.email.MailWire;

public static class ImapFlagSyntax
{
    public const int MaximumKeywordsPerMessage = 128;
    public const int MaximumKeywordLength = 255;

    public static bool TryValidate(IReadOnlyList<string> flags, out string failure)
    {
        foreach (var flag in flags)
        {
            if (flag is null)
            {
                failure = "Invalid flag list";
                return false;
            }
            if (flag.ToUpperInvariant() is "\\SEEN" or "\\DELETED" or "\\FLAGGED"
                or "\\DRAFT" or "\\ANSWERED")
            {
                continue;
            }
            if (flag.Equals("\\Recent", StringComparison.OrdinalIgnoreCase))
            {
                failure = "The \\Recent flag cannot be changed";
                return false;
            }
            if (flag.StartsWith('\\') || !IsValidKeyword(flag))
            {
                failure = "Invalid flag list";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    public static bool IsValidKeyword(string? keyword)
    {
        if (keyword is null || keyword.Length is 0 or > MaximumKeywordLength
            || keyword[0] == '\\')
        {
            return false;
        }

        foreach (var character in keyword)
        {
            if (character <= ' '
                || character >= '\u007f'
                || character is '(' or ')' or '{' or '%' or '*' or ']')
            {
                return false;
            }
        }

        return true;
    }
}
