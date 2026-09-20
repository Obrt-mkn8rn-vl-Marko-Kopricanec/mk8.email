using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapMailboxView(
    Guid Id,
    string FullName,
    string Name,
    Guid? ParentId,
    string? Role,
    long SortOrder,
    bool IsSubscribed,
    int TotalEmails,
    int UnreadEmails,
    int TotalThreads,
    int UnreadThreads)
{
    public bool IsProtected => Role is "inbox" or "sent" or "drafts" or "trash" or "junk";
}

internal sealed class JmapMailboxStore(EmailDbContext database)
{
    public async Task<IReadOnlyList<JmapMailboxView>> LoadAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var folders = await database.Folders
            .AsNoTracking()
            .Where(folder => folder.InboxId == accountId)
            .OrderBy(folder => folder.Name)
            .Select(folder => new
            {
                folder.Id,
                folder.Name,
                folder.JmapRole,
                folder.SuppressDefaultJmapRole,
                folder.SortOrder,
                folder.IsSubscribed,
            })
            .ToListAsync(cancellationToken);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.InboxId == accountId && !email.IsDeleted)
            .Select(email => new MailboxMessage(
                email.Id,
                email.FolderId,
                email.IsRead,
                email.IsDraft,
                email.ThreadObjectId))
            .ToListAsync(cancellationToken);

        var idByName = folders.ToDictionary(
            folder => folder.Name,
            folder => folder.Id,
            StringComparer.OrdinalIgnoreCase);
        var messagesByFolder = messages.ToLookup(message => message.FolderId);
        var result = new List<JmapMailboxView>(folders.Count);
        foreach (var folder in folders)
        {
            var folderMessages = messagesByFolder[folder.Id].ToArray();
            var unreadMessages = folderMessages
                .Where(message => !message.IsRead && !message.IsDraft)
                .ToArray();
            var parentName = ParentName(folder.Name);
            Guid? parentId = parentName is not null
                && idByName.TryGetValue(parentName, out var resolvedParentId)
                    ? resolvedParentId
                    : null;
            result.Add(new JmapMailboxView(
                folder.Id,
                folder.Name,
                LeafName(folder.Name),
                parentId,
                EffectiveRole(
                    folder.JmapRole,
                    folder.SuppressDefaultJmapRole,
                    folder.Name),
                folder.SortOrder,
                folder.IsSubscribed,
                folderMessages.Length,
                unreadMessages.Length,
                folderMessages.Select(ThreadKey).Distinct(StringComparer.Ordinal).Count(),
                unreadMessages.Select(ThreadKey).Distinct(StringComparer.Ordinal).Count()));
        }

        return result;
    }

    public Task<List<FolderDB>> LoadTrackedFoldersAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        database.Folders
            .Where(folder => folder.InboxId == accountId)
            .OrderBy(folder => folder.Name)
            .ToListAsync(cancellationToken);

    public static string LeafName(string fullName)
    {
        var separator = fullName.LastIndexOf('/');
        return separator < 0 ? fullName : fullName[(separator + 1)..];
    }

    public static string? ParentName(string fullName)
    {
        var separator = fullName.LastIndexOf('/');
        return separator < 0 ? null : fullName[..separator];
    }

    public static string? InferRole(string fullName)
    {
        if (ParentName(fullName) is not null)
            return null;
        return fullName.ToUpperInvariant() switch
        {
            "INBOX" => "inbox",
            "SENT" => "sent",
            "DRAFTS" => "drafts",
            "TRASH" => "trash",
            "SPAM" => "junk",
            _ => null,
        };
    }

    public static string? EffectiveRole(FolderDB folder) => EffectiveRole(
        folder.JmapRole,
        folder.SuppressDefaultJmapRole,
        folder.Name);

    private static string? EffectiveRole(
        string? storedRole,
        bool suppressDefaultRole,
        string fullName) =>
        storedRole ?? (suppressDefaultRole ? null : InferRole(fullName));

    private static string ThreadKey(MailboxMessage message) =>
        string.IsNullOrEmpty(message.ThreadObjectId)
            ? message.Id.ToString("N")
            : message.ThreadObjectId;

    private sealed record MailboxMessage(
        Guid Id,
        Guid FolderId,
        bool IsRead,
        bool IsDraft,
        string? ThreadObjectId);
}
