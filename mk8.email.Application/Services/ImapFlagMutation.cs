using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

internal static class ImapFlagMutation
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

    public static bool TryApply(
        EmailDB email,
        ImapFlagMutationMode mode,
        IReadOnlyList<string> flags,
        out string failure)
    {
        if (!Enum.IsDefined(mode))
        {
            failure = "Invalid STORE action";
            return false;
        }
        if (!TryValidate(flags, out failure))
            return false;

        var replace = mode == ImapFlagMutationMode.Replace;
        var remove = mode == ImapFlagMutationMode.Remove;
        var isRead = replace ? false : email.IsRead;
        var isDeleted = replace ? false : email.IsDeleted;
        var isFlagged = replace ? false : email.IsFlagged;
        var isDraft = replace ? false : email.IsDraft;
        var isAnswered = replace ? false : email.IsAnswered;
        var keywords = new HashSet<string>(
            replace
                ? []
                : (email.Keywords ?? []).Where(IsValidKeyword),
            StringComparer.OrdinalIgnoreCase);

        foreach (var flag in flags)
        {
            var value = !remove;
            switch (flag.ToUpperInvariant())
            {
                case "\\SEEN": isRead = value; break;
                case "\\DELETED": isDeleted = value; break;
                case "\\FLAGGED": isFlagged = value; break;
                case "\\DRAFT": isDraft = value; break;
                case "\\ANSWERED": isAnswered = value; break;
                default:
                    if (remove)
                        keywords.Remove(flag);
                    else
                        keywords.Add(flag);
                    break;
            }
        }

        if (keywords.Count > MaximumKeywordsPerMessage)
        {
            failure = "Too many keywords";
            return false;
        }

        email.IsRead = isRead;
        email.IsDeleted = isDeleted;
        email.IsFlagged = isFlagged;
        email.IsDraft = isDraft;
        email.IsAnswered = isAnswered;
        email.Keywords = keywords
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(keyword => keyword, StringComparer.Ordinal)
            .ToArray();
        failure = string.Empty;
        return true;
    }
}
