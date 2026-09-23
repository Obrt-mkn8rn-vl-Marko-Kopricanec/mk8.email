using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

internal sealed class ImapApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens,
    EmailDbContext database) : IImapApplicationService
{
    public async Task<ImapIdentityResult> AuthenticatePasswordAsync(
        ImapPasswordAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await authenticator.AuthenticateAsync(
            request.Username, request.Password, cancellationToken);
        return new ImapIdentityResult(user?.Id, user?.Username);
    }

    public async Task<ImapIdentityResult> AuthenticateOAuthAsync(
        ImapOAuthAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await oauthTokens.AuthenticateAccessTokenAsync(
            request.AccessToken, "imap", cancellationToken);
        return user is null
            || !string.Equals(request.Username, user.Username, StringComparison.OrdinalIgnoreCase)
            ? new ImapIdentityResult(null, null)
            : new ImapIdentityResult(user.Id, user.Username);
    }

    public async Task<ImapMailboxListResult> ListMailboxesAsync(
        ImapMailboxListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("The IMAP account identifier is invalid.", nameof(request));

        var query = database.Folders
            .AsNoTracking()
            .Where(folder => folder.Inbox.OwnerId == request.UserId);
        if (request.SubscribedOnly)
            query = query.Where(folder => folder.IsSubscribed);

        var folders = await query
            .Select(folder => new
            {
                InboxName = folder.Inbox.Name,
                folder.Inbox.Address.Domain,
                FolderName = folder.Name,
                OwnerUsername = folder.Inbox.Owner.Username,
                folder.IsSubscribed,
            })
            .OrderBy(folder => folder.Domain)
            .ThenBy(folder => folder.InboxName)
            .ThenBy(folder => folder.FolderName)
            .ToListAsync(cancellationToken);
        return new ImapMailboxListResult(folders
            .Select(folder => new ImapMailboxInfo(
                folder.InboxName,
                folder.Domain,
                folder.FolderName,
                string.Equals(
                    folder.OwnerUsername,
                    $"{folder.InboxName}@{folder.Domain}",
                    StringComparison.OrdinalIgnoreCase),
                folder.IsSubscribed))
            .ToList());
    }

    public async Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
        ImapMailboxStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MailboxNames);
        if (request.UserId == Guid.Empty || request.MailboxNames.Any(name => name is null))
            throw new ArgumentException("The IMAP mailbox status request is invalid.", nameof(request));

        var statuses = new Dictionary<string, ImapMailboxStatus>(StringComparer.Ordinal);
        foreach (var mailboxName in request.MailboxNames.Distinct(StringComparer.Ordinal))
        {
            var folder = await ImapMailboxResolver.ResolveFolderAsync(
                database, request.UserId, mailboxName, cancellationToken);
            if (folder is null)
                continue;

            var messageCount = request.IncludeMessageCount
                ? await database.Emails.CountAsync(
                    email => email.FolderId == folder.Id, cancellationToken)
                : (int?)null;
            var unseenCount = request.IncludeUnseenCount
                ? await database.Emails.CountAsync(
                    email => email.FolderId == folder.Id && !email.IsRead,
                    cancellationToken)
                : (int?)null;
            var sizeBytes = request.IncludeSize
                ? await database.Emails
                    .Where(email => email.FolderId == folder.Id)
                    .SumAsync(email => (long?)email.SizeBytes, cancellationToken) ?? 0
                : (long?)null;
            statuses.Add(mailboxName, new ImapMailboxStatus(
                folder.Id,
                folder.UidValidity,
                folder.NextUid,
                folder.HighestModSeq,
                folder.MailboxId,
                messageCount,
                unseenCount,
                sizeBytes));
        }

        return new ImapMailboxStatusResult(statuses);
    }

    public async Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
        ImapMailboxSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || string.IsNullOrEmpty(request.MailboxName))
            throw new ArgumentException("The IMAP mailbox subscription request is invalid.", nameof(request));

        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken);
        if (folder is null)
            return new ImapMailboxSubscriptionResult(false);

        if (folder.IsSubscribed != request.IsSubscribed)
        {
            folder.IsSubscribed = request.IsSubscribed;
            await database.SaveChangesAsync(cancellationToken);
        }

        return new ImapMailboxSubscriptionResult(true);
    }

    public async Task<ImapMailboxCreateResult> CreateMailboxAsync(
        ImapMailboxCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || string.IsNullOrEmpty(request.MailboxName))
            throw new ArgumentException("The IMAP mailbox creation request is invalid.", nameof(request));

        var location = await ImapMailboxResolver.ResolveLocationAsync(
            database, request.UserId, request.MailboxName, cancellationToken);
        if (location is null || !ImapMailboxResolver.IsValidFolderName(location.Value.FolderName))
            return new ImapMailboxCreateResult(ImapMailboxCreateDisposition.InvalidName, Guid.Empty, null);

        var exists = await database.Folders.AnyAsync(
            folder => folder.InboxId == location.Value.InboxId
                && folder.Name == location.Value.FolderName,
            cancellationToken);
        if (exists)
            return new ImapMailboxCreateResult(ImapMailboxCreateDisposition.AlreadyExists, Guid.Empty, null);

        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = location.Value.FolderName,
            InboxId = location.Value.InboxId,
        };
        database.Folders.Add(folder);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.Entry(folder).State = EntityState.Detached;
            if (await database.Folders.AsNoTracking().AnyAsync(
                    candidate => candidate.InboxId == location.Value.InboxId
                        && candidate.Name == location.Value.FolderName,
                    cancellationToken))
            {
                return new ImapMailboxCreateResult(
                    ImapMailboxCreateDisposition.AlreadyExists, Guid.Empty, null);
            }

            throw;
        }

        return new ImapMailboxCreateResult(
            ImapMailboxCreateDisposition.Created, folder.Id, folder.MailboxId);
    }
}
