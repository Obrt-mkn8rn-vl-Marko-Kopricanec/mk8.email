using System.Text;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Protocol;

internal static class MailboxName
{
    public static bool IsValid(string value)
    {
        if (value.Length is < 1 or > FolderDB.MaximumStoredNameLength
            || value[0] == '/'
            || value[^1] == '/'
            || value.Contains("//", StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || !IsWellFormedUnicode(value))
        {
            return false;
        }

        var components = value.Split('/');
        return components.Length <= FolderDB.MaximumHierarchyDepth
            && components.All(component => component.Length > 0
                && Encoding.UTF8.GetByteCount(component) <= FolderDB.MaximumLeafNameOctets);
    }

    public static string Normalize(string value) =>
        DefaultFolders.All.FirstOrDefault(folder =>
            string.Equals(folder, value, StringComparison.OrdinalIgnoreCase))
        ?? (IsWellFormedUnicode(value)
            ? value.Normalize(NormalizationForm.FormC)
            : value);

    public static bool IsWellFormedUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
                continue;
            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index]))
            {
                return false;
            }
        }
        return true;
    }
}
