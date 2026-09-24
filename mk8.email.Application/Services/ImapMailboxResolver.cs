using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

internal static class ImapMailboxResolver
{
    public static bool IsValidFolderName(string folderName)
    {
        if (folderName.Length is < 1 or > FolderDB.MaximumStoredNameLength
            || folderName[0] == '/'
            || folderName[^1] == '/'
            || folderName.Contains("//", StringComparison.Ordinal)
            || folderName.Any(char.IsControl))
        {
            return false;
        }

        var components = folderName.Split('/');
        return components.Length <= FolderDB.MaximumHierarchyDepth
            && components.All(component =>
                component.Length > 0
                && Encoding.UTF8.GetByteCount(component) <= FolderDB.MaximumLeafNameOctets);
    }

    public static bool IsSystemFolder(string folderName) =>
        DefaultFolders.All.Any(systemName =>
            string.Equals(systemName, folderName, StringComparison.OrdinalIgnoreCase));

    public static async Task<FolderDB?> ResolveFolderAsync(
        EmailDbContext database,
        Guid userId,
        string mailboxName,
        CancellationToken cancellationToken)
    {
        var location = await ResolveLocationAsync(database, userId, mailboxName, cancellationToken).ConfigureAwait(false);
        if (location is null)
            return null;

        return await database.Folders.FirstOrDefaultAsync(
            folder => folder.InboxId == location.Value.InboxId
                && folder.Name == location.Value.FolderName,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ImapMailboxLocation?> ResolveLocationAsync(
        EmailDbContext database,
        Guid userId,
        string mailboxName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(mailboxName))
            return null;

        var username = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Username)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (username is null)
            return null;

        var qualifiedParts = mailboxName.Split('/', 3);
        if (qualifiedParts.Length == 3)
        {
            var qualifiedInboxId = await database.Inboxes
                .AsNoTracking()
                .Where(inbox => inbox.OwnerId == userId
                    && inbox.Name == qualifiedParts[0]
                    && inbox.Address.Domain == qualifiedParts[1])
                .Select(inbox => (Guid?)inbox.Id)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (qualifiedInboxId is not null)
                return new ImapMailboxLocation(qualifiedInboxId.Value, qualifiedParts[2]);
        }

        var separator = username.LastIndexOf('@');
        if (separator <= 0 || separator == username.Length - 1)
            return null;

        var primaryLocalPart = username[..separator];
        var primaryDomain = username[(separator + 1)..];
        var primaryInboxId = await database.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.OwnerId == userId
                && inbox.Name == primaryLocalPart
                && inbox.Address.Domain == primaryDomain)
            .Select(inbox => (Guid?)inbox.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return primaryInboxId is null
            ? null
            : new ImapMailboxLocation(
                primaryInboxId.Value,
                NormalizePrimaryFolderName(mailboxName));
    }

    private static string NormalizePrimaryFolderName(string folderName)
    {
        if (string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase))
            return DefaultFolders.Inbox;

        return DefaultFolders.All.FirstOrDefault(
            name => string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)) ?? folderName;
    }
}
