using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

internal sealed class ImapApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens,
    EmailDbContext database,
    MailboxMessageContentService content,
    LargeObjectTransactionEffects effects,
    ILogger<ImapApplicationService> logger) : IImapApplicationService
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

    public async Task<ImapMailboxRenameResult> RenameMailboxAsync(
        ImapMailboxRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty
            || string.IsNullOrEmpty(request.OldName)
            || string.IsNullOrEmpty(request.NewName))
        {
            throw new ArgumentException("The IMAP mailbox rename request is invalid.", nameof(request));
        }

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.OldName, cancellationToken);
        if (folder is null)
            return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.NotFound);
        if (ImapMailboxResolver.IsSystemFolder(folder.Name))
            return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.SystemFolder);

        var destination = await ImapMailboxResolver.ResolveLocationAsync(
            database, request.UserId, request.NewName, cancellationToken);
        if (destination is null
            || destination.Value.InboxId != folder.InboxId
            || !ImapMailboxResolver.IsValidFolderName(destination.Value.FolderName))
        {
            return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.InvalidDestination);
        }

        var oldFolderName = folder.Name;
        var affected = await database.Folders
            .Where(candidate => candidate.InboxId == folder.InboxId
                && (candidate.Name == oldFolderName
                    || candidate.Name.StartsWith(oldFolderName + "/")))
            .ToListAsync(cancellationToken);
        var renamed = affected.ToDictionary(
            candidate => candidate.Id,
            candidate => destination.Value.FolderName + candidate.Name[oldFolderName.Length..]);
        if (renamed.Values.Any(name => !ImapMailboxResolver.IsValidFolderName(name)))
            return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.InvalidDestination);

        var renamedNames = renamed.Values.ToHashSet(StringComparer.Ordinal);
        var affectedIds = affected.Select(candidate => candidate.Id).ToHashSet();
        var existingNames = await database.Folders
            .AsNoTracking()
            .Where(candidate => candidate.InboxId == folder.InboxId
                && !affectedIds.Contains(candidate.Id))
            .Select(candidate => candidate.Name)
            .ToListAsync(cancellationToken);
        if (existingNames.Any(renamedNames.Contains))
            return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.AlreadyExists);

        foreach (var candidate in affected)
            candidate.Name = renamed[candidate.Id];
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.Renamed);
    }

    public async Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
        ImapMailboxDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || string.IsNullOrEmpty(request.MailboxName))
            throw new ArgumentException("The IMAP mailbox deletion request is invalid.", nameof(request));

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken);
        if (folder is null)
            return new ImapMailboxDeleteResult(ImapMailboxDeleteDisposition.NotFound, Guid.Empty);
        if (ImapMailboxResolver.IsSystemFolder(folder.Name))
            return new ImapMailboxDeleteResult(ImapMailboxDeleteDisposition.SystemFolder, Guid.Empty);

        var marker = effects.Mark();
        var commitAttempted = false;
        try
        {
            var messages = await database.Emails
                .Where(email => email.FolderId == folder.Id)
                .ToListAsync(cancellationToken);
            foreach (var message in messages)
                content.DeleteOnCommit(message);
            database.Folders.Remove(folder);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            await effects.CommitAsync(marker);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogWarning(rollbackException, "Could not roll back IMAP mailbox deletion");
                }
            }
            if (commitAttempted)
                effects.Discard(marker);
            else
                await effects.RollbackAsync(marker);
            throw;
        }

        return new ImapMailboxDeleteResult(ImapMailboxDeleteDisposition.Deleted, folder.Id);
    }

    public async Task<ImapMailboxSelectResult> SelectMailboxAsync(
        ImapMailboxSelectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || string.IsNullOrEmpty(request.MailboxName))
            throw new ArgumentException("The IMAP mailbox selection request is invalid.", nameof(request));

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken);
        if (folder is null)
            return new ImapMailboxSelectResult(null);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == folder.Id)
            .OrderBy(email => email.Uid)
            .Select(email => new
            {
                email.Uid,
                email.ModSeq,
                email.IsRead,
                email.IsDeleted,
                email.IsFlagged,
                email.IsDraft,
                email.IsAnswered,
                email.Keywords,
            })
            .ToListAsync(cancellationToken);
        var firstUnseenIndex = messages.FindIndex(message => !message.IsRead);
        var keywords = messages
            .SelectMany(message => message.Keywords ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vanishedUids = new List<int>();
        var changedMessages = new List<ImapChangedMessage>();
        if (request.QresyncUidValidity == folder.UidValidity
            && request.QresyncModSeq is not null)
        {
            vanishedUids = await database.ExpungedUids
                .AsNoTracking()
                .Where(expunged => expunged.FolderId == folder.Id
                    && expunged.ModSeq > request.QresyncModSeq.Value)
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.Uid)
                .ToListAsync(cancellationToken);
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (message.ModSeq <= request.QresyncModSeq.Value)
                    continue;
                changedMessages.Add(new ImapChangedMessage(
                    index + 1,
                    message.Uid,
                    message.ModSeq,
                    message.IsRead,
                    message.IsDeleted,
                    message.IsFlagged,
                    message.IsDraft,
                    message.IsAnswered,
                    message.Keywords ?? []));
            }
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new ImapMailboxSelectResult(new ImapSelectedMailbox(
            folder.Id,
            folder.UidValidity,
            folder.NextUid,
            folder.HighestModSeq,
            folder.MailboxId,
            messages.Count,
            firstUnseenIndex < 0 ? null : firstUnseenIndex + 1,
            keywords,
            vanishedUids,
            changedMessages));
    }

    public async Task<ImapExpungeResult> ExpungeDeletedAsync(
        ImapExpungeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.FolderId == Guid.Empty)
            throw new ArgumentException("The IMAP expunge request is invalid.", nameof(request));

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var folder = await database.Folders.SingleOrDefaultAsync(
            candidate => candidate.Id == request.FolderId
                && candidate.Inbox.OwnerId == request.UserId,
            cancellationToken);
        if (folder is null)
            return new ImapExpungeResult(false, []);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == folder.Id)
            .OrderBy(email => email.Uid)
            .Select(email => new EmailDB
            {
                Id = email.Id,
                Uid = email.Uid,
                IsDeleted = email.IsDeleted,
                SizeBytes = email.SizeBytes,
                RawMessageObjectProvider = email.RawMessageObjectProvider,
                RawMessageObjectName = email.RawMessageObjectName,
                RawMessageObjectSha256 = email.RawMessageObjectSha256,
                RawMessageObjectEntityTag = email.RawMessageObjectEntityTag,
            })
            .ToListAsync(cancellationToken);
        var expunged = new List<ImapExpungedMessage>();
        var marker = effects.Mark();
        var commitAttempted = false;
        try
        {
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (!message.IsDeleted)
                    continue;

                folder.HighestModSeq++;
                database.ExpungedUids.Add(new ExpungedUidDB
                {
                    Id = Guid.CreateVersion7(),
                    Uid = message.Uid,
                    ModSeq = folder.HighestModSeq,
                    FolderId = folder.Id,
                });
                content.DeleteOnCommit(message);
                database.Emails.Remove(new EmailDB { Id = message.Id });
                expunged.Add(new ImapExpungedMessage(
                    index + 1 - expunged.Count, message.Uid));
            }

            if (expunged.Count > 0)
                await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            await effects.CommitAsync(marker);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogWarning(rollbackException, "Could not roll back IMAP expunge");
                }
            }
            if (commitAttempted)
                effects.Discard(marker);
            else
                await effects.RollbackAsync(marker);
            throw;
        }

        return new ImapExpungeResult(true, expunged);
    }

    public async Task<ImapQuotaResult> GetQuotaAsync(
        ImapQuotaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("The IMAP quota request is invalid.", nameof(request));

        if (request.MailboxName is not null
            && await ImapMailboxResolver.ResolveFolderAsync(
                database, request.UserId, request.MailboxName, cancellationToken) is null)
        {
            return new ImapQuotaResult(false, 0, 0);
        }

        var quotaBytes = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == request.UserId)
            .Select(user => (long?)user.QuotaBytes)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var usedBytes = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == request.UserId)
            .SumAsync(email => (long?)email.SizeBytes, cancellationToken) ?? 0;
        return new ImapQuotaResult(true, usedBytes, quotaBytes);
    }

    public async Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
        ImapIdleSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.FolderId == Guid.Empty)
            throw new ArgumentException("The IMAP IDLE snapshot request is invalid.", nameof(request));

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        var highestModSeq = await database.Folders
            .AsNoTracking()
            .Where(folder => folder.Id == request.FolderId
                && folder.Inbox.OwnerId == request.UserId)
            .Select(folder => (long?)folder.HighestModSeq)
            .SingleOrDefaultAsync(cancellationToken);
        if (highestModSeq is null)
            return new ImapIdleSnapshotResult(false, 0, []);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == request.FolderId)
            .OrderBy(email => email.Uid)
            .Select(email => new
            {
                email.Id,
                email.Uid,
                email.ModSeq,
                email.IsRead,
                email.IsDeleted,
                email.IsFlagged,
                email.IsDraft,
                email.IsAnswered,
                email.Keywords,
            })
            .ToListAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new ImapIdleSnapshotResult(
            true,
            highestModSeq.Value,
            messages.Select(message => new ImapIdleMessage(
                message.Id,
                message.Uid,
                message.ModSeq,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.Keywords ?? [])).ToList());
    }
}
