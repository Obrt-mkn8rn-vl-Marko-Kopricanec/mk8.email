using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal static class ImapFlagMutation
{
    public const int MaximumKeywordsPerMessage = ImapFlagSyntax.MaximumKeywordsPerMessage;
    public const int MaximumKeywordLength = ImapFlagSyntax.MaximumKeywordLength;

    public static bool TryValidate(IReadOnlyList<string> flags, out string failure) =>
        ImapFlagSyntax.TryValidate(flags, out failure);

    public static bool IsValidKeyword(string? keyword) => ImapFlagSyntax.IsValidKeyword(keyword);

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
