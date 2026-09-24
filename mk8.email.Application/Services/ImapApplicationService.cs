using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Configuration;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal sealed class ImapApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens,
    EmailDbContext database,
    MailboxMessageContentService content,
    LargeObjectTransactionEffects effects,
    ILogger<ImapApplicationService> logger,
    EnvironmentConfig? environment = null) : IImapApplicationService
{
    private const int MaximumMultiAppendMessages = 20;
    private const int FetchScanPageSize = 256;
    private const int FetchMetadataPageSize = 128;
    private const int FetchContentPageSize = 8;
    private const int FetchContentPageBytes = 16 * 1024 * 1024;

    public async Task<ImapIdentityResult> AuthenticatePasswordAsync(
        ImapPasswordAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await authenticator.AuthenticateAsync(
            request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        return new ImapIdentityResult(user?.Id, user?.Username);
    }

    public async Task<ImapIdentityResult> AuthenticateOAuthAsync(
        ImapOAuthAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await oauthTokens.AuthenticateAccessTokenAsync(
            request.AccessToken, "imap", cancellationToken).ConfigureAwait(false);
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
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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
        if (request.MailboxNames is null)
            throw new ArgumentNullException(nameof(request), "The IMAP mailbox names are required.");
        if (request.UserId == Guid.Empty || request.MailboxNames.Any(name => name is null))
            throw new ArgumentException("The IMAP mailbox status request is invalid.", nameof(request));

        var statuses = new Dictionary<string, ImapMailboxStatus>(StringComparer.Ordinal);
        foreach (var mailboxName in request.MailboxNames.Distinct(StringComparer.Ordinal))
        {
            var folder = await ImapMailboxResolver.ResolveFolderAsync(
                database, request.UserId, mailboxName, cancellationToken).ConfigureAwait(false);
            if (folder is null)
                continue;

            var messageCount = request.IncludeMessageCount
                ? await database.Emails.CountAsync(
                    email => email.FolderId == folder.Id, cancellationToken)
.ConfigureAwait(false) : (int?)null;
            var unseenCount = request.IncludeUnseenCount
                ? await database.Emails.CountAsync(
                    email => email.FolderId == folder.Id && !email.IsRead,
                    cancellationToken)
.ConfigureAwait(false) : (int?)null;
            var sizeBytes = request.IncludeSize
                ? await database.Emails
                    .Where(email => email.FolderId == folder.Id)
                    .SumAsync(email => (long?)email.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0
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
            database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false);
        if (folder is null)
            return new ImapMailboxSubscriptionResult(false);

        if (folder.IsSubscribed != request.IsSubscribed)
        {
            folder.IsSubscribed = request.IsSubscribed;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false);
        if (location is null || !ImapMailboxResolver.IsValidFolderName(location.Value.FolderName))
            return new ImapMailboxCreateResult(ImapMailboxCreateDisposition.InvalidName, Guid.Empty, null);

        var exists = await database.Folders.AnyAsync(
            folder => folder.InboxId == location.Value.InboxId
                && folder.Name == location.Value.FolderName,
            cancellationToken).ConfigureAwait(false);
        if (exists)
            return new ImapMailboxCreateResult(ImapMailboxCreateDisposition.AlreadyExists, Guid.Empty, null);

        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = location.Value.FolderName,
            InboxId = location.Value.InboxId,
        };
        await database.Folders.AddAsync(folder, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            database.Entry(folder).State = EntityState.Detached;
            if (await database.Folders.AsNoTracking().AnyAsync(
                    candidate => candidate.InboxId == location.Value.InboxId
                        && candidate.Name == location.Value.FolderName,
                    cancellationToken).ConfigureAwait(false))
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

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.OldName, cancellationToken).ConfigureAwait(false);
            if (folder is null)
                return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.NotFound);
            if (ImapMailboxResolver.IsSystemFolder(folder.Name))
                return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.SystemFolder);

            var destination = await ImapMailboxResolver.ResolveLocationAsync(
                database, request.UserId, request.NewName, cancellationToken).ConfigureAwait(false);
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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (existingNames.Any(renamedNames.Contains))
                return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.AlreadyExists);

            foreach (var candidate in affected)
                candidate.Name = renamed[candidate.Id];
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImapMailboxRenameResult(ImapMailboxRenameDisposition.Renamed);
        }
    }

    public async Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
        ImapMailboxDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || string.IsNullOrEmpty(request.MailboxName))
            throw new ArgumentException("The IMAP mailbox deletion request is invalid.", nameof(request));

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using var transactionLifetime = transaction;
#pragma warning restore CA2007, MA0004
        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false);
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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var message in messages)
                content.DeleteOnCommit(message);
            database.Folders.Remove(folder);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    ApplicationServiceLog.ImapMailboxDeletionRollbackFailed(logger, rollbackException);
                }
            }
            if (commitAttempted)
                effects.Discard(marker);
            else
                await effects.RollbackAsync(marker).ConfigureAwait(false);
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

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using var transactionLifetime = transaction;
#pragma warning restore CA2007, MA0004
        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false);
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
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
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
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        var selection = request.UidSelection;
        if (selection is not null
            && (selection.Ranges is null) == (selection.SavedSearchUids is null))
        {
            throw new ArgumentException("The IMAP UID selection is invalid.", nameof(request));
        }
        if ((selection?.Ranges is { } requestedRanges
            && (requestedRanges.Count == 0
                || requestedRanges.Any(range => range is null
                    || range.Start is < 1 || range.End is < 1)))
            || (selection?.SavedSearchUids is { } savedUids
                && savedUids.Any(uid => uid < 1)))
        {
            throw new ArgumentException("The IMAP UID selection is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using var transactionLifetime = transaction;
#pragma warning restore CA2007, MA0004
        var folder = await database.Folders.FirstOrDefaultAsync(
            candidate => candidate.Id == request.FolderId
                && candidate.Inbox.OwnerId == request.UserId,
            cancellationToken).ConfigureAwait(false);
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
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var maximumUid = messages.Count > 0 ? messages[^1].Uid : 0;
        var resolvedRanges = selection?.Ranges is { } ranges
            ? ResolveMessageRanges(ranges.Select(range => (range.Start, range.End)), maximumUid)
            : null;
        var savedSearchUids = selection?.SavedSearchUids?.ToHashSet();
        var rangeIndex = 0;
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
                if (savedSearchUids is not null && !savedSearchUids.Contains(message.Uid))
                    continue;
                if (resolvedRanges is not null)
                {
                    while (rangeIndex < resolvedRanges.Count
                        && resolvedRanges[rangeIndex].End < message.Uid)
                    {
                        rangeIndex++;
                    }
                    if (rangeIndex == resolvedRanges.Count
                        || resolvedRanges[rangeIndex].Start > message.Uid)
                    {
                        continue;
                    }
                }

                folder.HighestModSeq++;
                await database.ExpungedUids.AddAsync(new ExpungedUidDB
                {
                    Id = Guid.CreateVersion7(),
                    Uid = message.Uid,
                    ModSeq = folder.HighestModSeq,
                    FolderId = folder.Id,
                }, cancellationToken).ConfigureAwait(false);
                content.DeleteOnCommit(message);
                database.Emails.Remove(new EmailDB { Id = message.Id });
                expunged.Add(new ImapExpungedMessage(
                    index + 1 - expunged.Count, message.Uid));
            }

            if (expunged.Count > 0)
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    ApplicationServiceLog.ImapExpungeRollbackFailed(logger, rollbackException);
                }
            }
            if (commitAttempted)
                effects.Discard(marker);
            else
                await effects.RollbackAsync(marker).ConfigureAwait(false);
            throw;
        }

        return new ImapExpungeResult(true, expunged);
    }

    private static List<(int Start, int End)> ResolveMessageRanges(
        IEnumerable<(int? Start, int? End)> ranges, int maximumIdentifier)
    {
        var sorted = ranges
            .Select(range =>
            {
                var start = range.Start ?? maximumIdentifier;
                var end = range.End ?? maximumIdentifier;
                return (Start: Math.Min(start, end), End: Math.Max(start, end));
            })
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToList();
        var merged = new List<(int Start, int End)>();
        foreach (var range in sorted)
        {
            if (merged.Count == 0 || range.Start > (long)merged[^1].End + 1)
            {
                merged.Add(range);
            }
            else
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            }
        }
        return merged;
    }

    public async Task<ImapStoreResult> StoreFlagsAsync(
        ImapStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.FolderId == Guid.Empty
            || request.Selection is null || request.Flags is null
            || !Enum.IsDefined(request.Mode))
        {
            throw new ArgumentException("The IMAP STORE request is invalid.", nameof(request));
        }
        var selection = request.Selection;
        if (!IsValidMessageSelection(selection)
            || !ImapFlagMutation.TryValidate(request.Flags, out _))
        {
            throw new ArgumentException("The IMAP STORE request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using var transactionLifetime = transaction;
#pragma warning restore CA2007, MA0004
        var folder = await database.Folders.FirstOrDefaultAsync(
            candidate => candidate.Id == request.FolderId
                && candidate.Inbox.OwnerId == request.UserId,
            cancellationToken).ConfigureAwait(false);
        if (folder is null)
            return new ImapStoreResult(ImapStoreDisposition.FolderNotFound, [], []);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == folder.Id)
            .OrderBy(email => email.Uid)
            .Select(email => new EmailDB
            {
                Id = email.Id,
                Uid = email.Uid,
                ModSeq = email.ModSeq,
                IsRead = email.IsRead,
                IsDeleted = email.IsDeleted,
                IsFlagged = email.IsFlagged,
                IsDraft = email.IsDraft,
                IsAnswered = email.IsAnswered,
                Keywords = email.Keywords,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var maximumIdentifier = request.UseUid
            ? messages.Count > 0 ? messages[^1].Uid : 0
            : messages.Count;
        var resolvedRanges = selection.Ranges is { } ranges
            ? ResolveMessageRanges(ranges.Select(range => (range.Start, range.End)), maximumIdentifier)
            : null;
        var savedSearchUids = selection.SavedSearchUids?.ToHashSet();
        var rangeIndex = 0;
        var modified = new List<int>();
        var applicable = new List<(EmailDB Email, int SequenceNumber)>();

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var identifier = request.UseUid ? message.Uid : index + 1;
            if (savedSearchUids is not null && !savedSearchUids.Contains(message.Uid))
                continue;
            if (resolvedRanges is not null)
            {
                while (rangeIndex < resolvedRanges.Count
                    && resolvedRanges[rangeIndex].End < identifier)
                {
                    rangeIndex++;
                }
                if (rangeIndex == resolvedRanges.Count
                    || resolvedRanges[rangeIndex].Start > identifier)
                {
                    continue;
                }
            }

            if (request.UnchangedSince is { } unchangedSince && message.ModSeq > unchangedSince)
            {
                modified.Add(identifier);
                continue;
            }
            if (!ImapFlagMutation.TryApply(message, request.Mode, request.Flags, out _))
                return new ImapStoreResult(ImapStoreDisposition.KeywordLimitExceeded, [], []);
            applicable.Add((message, index + 1));
        }

        var updated = new List<ImapChangedMessage>();
        if (applicable.Count > 0)
        {
            var newModSeq = ++folder.HighestModSeq;
            var trackedUpdates = new List<EmailDB>();
            foreach (var item in applicable)
            {
                trackedUpdates.Add(AttachFlagUpdate(database, item.Email, newModSeq));
                updated.Add(new ImapChangedMessage(
                    item.SequenceNumber,
                    item.Email.Uid,
                    newModSeq,
                    item.Email.IsRead,
                    item.Email.IsDeleted,
                    item.Email.IsFlagged,
                    item.Email.IsDraft,
                    item.Email.IsAnswered,
                    item.Email.Keywords ?? []));
            }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var update in trackedUpdates)
                database.Entry(update).State = EntityState.Detached;
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ImapStoreResult(ImapStoreDisposition.Stored, modified, updated);
    }

    private static EmailDB AttachFlagUpdate(EmailDbContext database, EmailDB metadata, long modSeq)
    {
        var update = new EmailDB
        {
            Id = metadata.Id,
            IsRead = metadata.IsRead,
            IsDeleted = metadata.IsDeleted,
            IsFlagged = metadata.IsFlagged,
            IsDraft = metadata.IsDraft,
            IsAnswered = metadata.IsAnswered,
            Keywords = metadata.Keywords?.ToArray() ?? [],
            ModSeq = modSeq,
        };
        database.Emails.Attach(update);
        var entry = database.Entry(update);
        entry.Property(email => email.IsRead).IsModified = true;
        entry.Property(email => email.IsDeleted).IsModified = true;
        entry.Property(email => email.IsFlagged).IsModified = true;
        entry.Property(email => email.IsDraft).IsModified = true;
        entry.Property(email => email.IsAnswered).IsModified = true;
        entry.Property(email => email.Keywords).IsModified = true;
        entry.Property(email => email.ModSeq).IsModified = true;
        return update;
    }

    public async Task<ImapMoveResult> MoveMessagesAsync(
        ImapMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.SourceFolderId == Guid.Empty
            || string.IsNullOrEmpty(request.DestinationMailboxName)
            || request.Selection is null || !IsValidMessageSelection(request.Selection))
        {
            throw new ArgumentException("The IMAP MOVE request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using var transactionLifetime = transaction;
#pragma warning restore CA2007, MA0004
        var source = await database.Folders.FirstOrDefaultAsync(
            folder => folder.Id == request.SourceFolderId
                && folder.Inbox.OwnerId == request.UserId,
            cancellationToken).ConfigureAwait(false);
        if (source is null)
            return new ImapMoveResult(ImapMoveDisposition.SourceNotFound, 0, [], [], []);
        var destination = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.DestinationMailboxName, cancellationToken).ConfigureAwait(false);
        if (destination is null)
            return new ImapMoveResult(ImapMoveDisposition.DestinationNotFound, 0, [], [], []);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == source.Id)
            .OrderBy(email => email.Uid)
            .Select(email => new EmailDB
            {
                Id = email.Id,
                FolderId = email.FolderId,
                Uid = email.Uid,
                ModSeq = email.ModSeq,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var maximumIdentifier = request.UseUid
            ? messages.Count > 0 ? messages[^1].Uid : 0
            : messages.Count;
        var resolvedRanges = request.Selection.Ranges is { } ranges
            ? ResolveMessageRanges(ranges.Select(range => (range.Start, range.End)), maximumIdentifier)
            : null;
        var savedSearchUids = request.Selection.SavedSearchUids?.ToHashSet();
        var rangeIndex = 0;
        var sourceUids = new List<int>();
        var destinationUids = new List<int>();
        var expungeSequenceNumbers = new List<int>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var identifier = request.UseUid ? message.Uid : index + 1;
            if (savedSearchUids is not null && !savedSearchUids.Contains(message.Uid))
                continue;
            if (resolvedRanges is not null)
            {
                while (rangeIndex < resolvedRanges.Count
                    && resolvedRanges[rangeIndex].End < identifier)
                {
                    rangeIndex++;
                }
                if (rangeIndex == resolvedRanges.Count
                    || resolvedRanges[rangeIndex].Start > identifier)
                {
                    continue;
                }
            }

            var sourceModSeq = ++source.HighestModSeq;
            await database.ExpungedUids.AddAsync(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = message.Uid,
                ModSeq = sourceModSeq,
                FolderId = source.Id,
            }, cancellationToken).ConfigureAwait(false);
            var destinationUid = destination.NextUid++;
            var destinationModSeq = ++destination.HighestModSeq;
            AttachMoveUpdate(database, message, destination.Id, destinationUid, destinationModSeq);
            expungeSequenceNumbers.Add(index + 1 - sourceUids.Count);
            sourceUids.Add(message.Uid);
            destinationUids.Add(destinationUid);
        }

        if (sourceUids.Count > 0)
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ImapMoveResult(ImapMoveDisposition.Moved,
            destination.UidValidity, sourceUids, destinationUids, expungeSequenceNumbers);
    }

    private static void AttachMoveUpdate(
        EmailDbContext database,
        EmailDB metadata,
        Guid destinationFolderId,
        int destinationUid,
        long destinationModSeq)
    {
        var update = new EmailDB
        {
            Id = metadata.Id,
            FolderId = destinationFolderId,
            Uid = destinationUid,
            ModSeq = destinationModSeq,
        };
        database.Emails.Attach(update);
        var entry = database.Entry(update);
        entry.Property(email => email.FolderId).IsModified = true;
        entry.Property(email => email.Uid).IsModified = true;
        entry.Property(email => email.ModSeq).IsModified = true;
    }

    private static bool IsValidMessageSelection(ImapMessageSelection selection) =>
        (selection.Ranges is null) != (selection.SavedSearchUids is null)
        && (selection.Ranges is not { } ranges
            || ranges.Count > 0 && ranges.All(range => range is not null
                && range.Start is not < 1 && range.End is not < 1))
        && (selection.SavedSearchUids is not { } savedUids
            || savedUids.All(uid => uid > 0));

    public async Task<ImapCopyResult> CopyMessagesAsync(
        ImapCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.SourceFolderId == Guid.Empty
            || string.IsNullOrEmpty(request.DestinationMailboxName)
            || request.Selection is null || !IsValidMessageSelection(request.Selection))
        {
            throw new ArgumentException("The IMAP COPY request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using var transactionLifetime = transaction;
#pragma warning restore CA2007, MA0004
        var sourceFolder = await database.Folders.FirstOrDefaultAsync(
            folder => folder.Id == request.SourceFolderId
                && folder.Inbox.OwnerId == request.UserId,
            cancellationToken).ConfigureAwait(false);
        if (sourceFolder is null)
            return new ImapCopyResult(ImapCopyDisposition.SourceNotFound, 0, [], []);
        var destination = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.DestinationMailboxName, cancellationToken).ConfigureAwait(false);
        if (destination is null)
            return new ImapCopyResult(ImapCopyDisposition.DestinationNotFound, 0, [], []);

        var messages = await database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == sourceFolder.Id)
            .OrderBy(email => email.Uid)
            .Select(email => new EmailDB
            {
                Id = email.Id,
                FolderId = email.FolderId,
                Uid = email.Uid,
                SizeBytes = email.SizeBytes,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var maximumIdentifier = request.UseUid
            ? messages.Count > 0 ? messages[^1].Uid : 0
            : messages.Count;
        var resolvedRanges = request.Selection.Ranges is { } ranges
            ? ResolveMessageRanges(ranges.Select(range => (range.Start, range.End)), maximumIdentifier)
            : null;
        var savedSearchUids = request.Selection.SavedSearchUids?.ToHashSet();
        var rangeIndex = 0;
        var selected = new List<EmailDB>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var identifier = request.UseUid ? message.Uid : index + 1;
            if (savedSearchUids is not null && !savedSearchUids.Contains(message.Uid))
                continue;
            if (resolvedRanges is not null)
            {
                while (rangeIndex < resolvedRanges.Count
                    && resolvedRanges[rangeIndex].End < identifier)
                {
                    rangeIndex++;
                }
                if (rangeIndex == resolvedRanges.Count
                    || resolvedRanges[rangeIndex].Start > identifier)
                {
                    continue;
                }
            }
            selected.Add(message);
        }
        if (selected.Count == 0)
            return new ImapCopyResult(ImapCopyDisposition.Copied,
                destination.UidValidity, [], []);

        long addedBytes = 0;
        foreach (var message in selected)
        {
            if (message.SizeBytes < 0 || addedBytes > long.MaxValue - message.SizeBytes)
                return new ImapCopyResult(ImapCopyDisposition.InvalidSourceSize, 0, [], []);
            addedBytes += message.SizeBytes;
        }
        var quotaBytes = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == request.UserId)
            .Select(user => (long?)user.QuotaBytes)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (quotaBytes is null)
            return new ImapCopyResult(ImapCopyDisposition.SourceNotFound, 0, [], []);
        if (quotaBytes > 0)
        {
            var usedBytes = await database.Emails
                .AsNoTracking()
                .Where(email => email.Folder.Inbox.OwnerId == request.UserId)
                .SumAsync(email => (long?)email.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0;
            if (usedBytes >= quotaBytes || addedBytes > quotaBytes - usedBytes)
                return new ImapCopyResult(ImapCopyDisposition.OverQuota, 0, [], []);
        }

        var marker = effects.Mark();
        var commitAttempted = false;
        var sourceUids = new List<int>(selected.Count);
        var destinationUids = new List<int>(selected.Count);
        try
        {
            foreach (var metadata in selected)
            {
                var source = await database.Emails
                    .AsNoTracking()
                    .SingleAsync(email => email.Id == metadata.Id
                        && email.FolderId == sourceFolder.Id, cancellationToken).ConfigureAwait(false);
                var rawMessage = await content.ReadAsync(source, cancellationToken).ConfigureAwait(false);
                var destinationUid = destination.NextUid++;
                var destinationModSeq = ++destination.HighestModSeq;
                var copy = new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = source.Sender,
                    Recipient = source.Recipient,
                    Subject = source.Subject,
                    Body = string.Empty,
                    MessageId = source.MessageId,
                    InReplyTo = source.InReplyTo,
                    Cc = source.Cc,
                    EmailObjectId = string.IsNullOrEmpty(source.EmailObjectId)
                        ? source.Id.ToString("N")
                        : source.EmailObjectId,
                    ThreadObjectId = source.ThreadObjectId,
                    IsRead = source.IsRead,
                    IsDeleted = false,
                    IsFlagged = source.IsFlagged,
                    IsDraft = source.IsDraft,
                    IsAnswered = source.IsAnswered,
                    Keywords = source.Keywords?.ToArray() ?? [],
                    ReceivedAt = source.ReceivedAt,
                    Uid = destinationUid,
                    ModSeq = destinationModSeq,
                    FolderId = destination.Id,
                };
                await content.SetAsync(copy, rawMessage, cancellationToken).ConfigureAwait(false);
                await database.Emails.AddAsync(copy, cancellationToken).ConfigureAwait(false);
                sourceUids.Add(source.Uid);
                destinationUids.Add(destinationUid);
            }

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    ApplicationServiceLog.ImapCopyRollbackFailed(logger, rollbackException);
                }
            }
            if (commitAttempted)
                effects.Discard(marker);
            else
                await effects.RollbackAsync(marker).ConfigureAwait(false);
            throw;
        }

        return new ImapCopyResult(ImapCopyDisposition.Copied,
            destination.UidValidity, sourceUids, destinationUids);
    }

    public async Task<ImapAppendPreflightResult> CheckAppendCapacityAsync(
        ImapAppendPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty
            || string.IsNullOrEmpty(request.MailboxName)
            || request.AddedBytes < 0)
        {
            throw new ArgumentException("The IMAP APPEND preflight request is invalid.",
                nameof(request));
        }

        var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false);
        if (folder is null)
            return new ImapAppendPreflightResult(ImapAppendPreflightDisposition.MailboxNotFound);

        var quotaBytes = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == request.UserId)
            .Select(user => (long?)user.QuotaBytes)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (quotaBytes is null)
            return new ImapAppendPreflightResult(ImapAppendPreflightDisposition.MailboxNotFound);
        if (quotaBytes <= 0)
            return new ImapAppendPreflightResult(ImapAppendPreflightDisposition.Ready);

        var usedBytes = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == request.UserId)
            .SumAsync(email => (long?)email.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0;
        return new ImapAppendPreflightResult(
            usedBytes < quotaBytes && request.AddedBytes <= quotaBytes - usedBytes
                ? ImapAppendPreflightDisposition.Ready
                : ImapAppendPreflightDisposition.OverQuota);
    }

    public async Task<ImapAppendResult> AppendMessagesAsync(
        ImapAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null)
            throw new ArgumentNullException(nameof(request), "The IMAP append messages are required.");
        var maximumMessageSize = environment?.Limits.MaxMessageSizeBytes ?? 10 * 1024 * 1024;
        if (request.UserId == Guid.Empty
            || string.IsNullOrEmpty(request.MailboxName)
            || request.Messages.Count is 0 or > MaximumMultiAppendMessages
            || request.Messages.Any(message => message is null
                || message.MessageId == Guid.Empty
                || message.Flags is null
                || message.RawMessage is null
                || message.RawMessage.Length == 0)
            || request.Messages.Select(message => message.MessageId).Distinct().Count()
                != request.Messages.Count)
        {
            throw new ArgumentException("The IMAP APPEND request is invalid.", nameof(request));
        }

        long addedBytes = 0;
        foreach (var message in request.Messages)
        {
            if (message.RawMessage.Length > maximumMessageSize - addedBytes)
                throw new ArgumentException("The IMAP APPEND request exceeds the size limit.",
                    nameof(request));
            addedBytes += message.RawMessage.Length;
            if (!ImapFlagMutation.TryValidate(message.Flags, out _)
                || message.Flags.Where(flag => !flag.StartsWith('\\'))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    > ImapFlagMutation.MaximumKeywordsPerMessage)
            {
                return new ImapAppendResult(ImapAppendDisposition.InvalidFlags, 0, []);
            }
            var rawText = MailWireEncoding.Instance.GetString(message.RawMessage);
            if (rawText.Contains('\0', StringComparison.Ordinal)
                || !ImapAppendContent.TryPrepareForParsing(
                    rawText, request.Utf8Enabled, out _))
            {
                return new ImapAppendResult(ImapAppendDisposition.InvalidContent, 0, []);
            }
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folder = await ImapMailboxResolver.ResolveFolderAsync(
            database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false);
            if (folder is null)
                return new ImapAppendResult(ImapAppendDisposition.MailboxNotFound, 0, []);

            var ids = request.Messages.Select(message => message.MessageId).ToArray();
            var existing = await database.Emails
                .AsNoTracking()
                .Where(email => ids.Contains(email.Id))
                .Select(email => new
                {
                    email.Id,
                    email.FolderId,
                    email.Uid,
                    email.SizeBytes,
                    email.RawMessageObjectSha256,
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                if (existing.Count != request.Messages.Count)
                    throw new InvalidOperationException("The IMAP APPEND message identities conflict.");
                var byId = existing.ToDictionary(email => email.Id);
                foreach (var message in request.Messages)
                {
                    var stored = byId[message.MessageId];
                    var hash = Convert.ToHexStringLower(SHA256.HashData(message.RawMessage));
                    if (stored.FolderId != folder.Id
                        || stored.Uid < 1
                        || stored.SizeBytes != message.RawMessage.Length
                        || !string.Equals(stored.RawMessageObjectSha256, hash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The IMAP APPEND message identities conflict.");
                    }
                }

                return new ImapAppendResult(ImapAppendDisposition.Appended,
                    folder.UidValidity,
                    request.Messages.Select(message => byId[message.MessageId].Uid).ToList());
            }

            var quotaBytes = await database.Users
                .AsNoTracking()
                .Where(user => user.Id == request.UserId)
                .Select(user => (long?)user.QuotaBytes)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (quotaBytes is null)
                return new ImapAppendResult(ImapAppendDisposition.MailboxNotFound, 0, []);
            if (quotaBytes > 0)
            {
                var usedBytes = await database.Emails
                    .AsNoTracking()
                    .Where(email => email.Folder.Inbox.OwnerId == request.UserId)
                    .SumAsync(email => (long?)email.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0;
                if (usedBytes >= quotaBytes || addedBytes > quotaBytes - usedBytes)
                    return new ImapAppendResult(ImapAppendDisposition.OverQuota, 0, []);
            }

            var marker = effects.Mark();
            var commitAttempted = false;
            var uids = new List<int>(request.Messages.Count);
            try
            {
                foreach (var message in request.Messages)
                {
                    var rawText = MailWireEncoding.Instance.GetString(message.RawMessage);
                    if (!ImapAppendContent.TryPrepareForParsing(
                        rawText, request.Utf8Enabled, out var parsingText))
                    {
                        throw new InvalidOperationException("The IMAP APPEND content changed after validation.");
                    }
                    var (subject, body, headers) = MailMessageParser.Parse(parsingText);
                    var messageId = MailMessageParser.ExtractHeaderValue(headers, "Message-ID");
                    var inReplyTo = MailMessageParser.ExtractHeaderValue(headers, "In-Reply-To");
                    var email = new EmailDB
                    {
                        Id = message.MessageId,
                        Sender = MailMessageParser.ExtractHeaderValue(headers, "From"),
                        Recipient = MailMessageParser.ExtractHeaderValue(headers, "To"),
                        Subject = subject.Length > 998 ? subject[..998] : subject,
                        Body = body,
                        RawHeaders = headers,
                        SizeBytes = message.RawMessage.Length,
                        MessageId = messageId,
                        InReplyTo = inReplyTo,
                        Cc = MailMessageParser.ExtractHeaderValue(headers, "Cc"),
                        EmailObjectId = Guid.CreateVersion7().ToString("N"),
                        ThreadObjectId = ImapAppendContent.GenerateThreadObjectId(
                            inReplyTo, messageId),
                        ReceivedAt = message.InternalDate ?? DateTime.UtcNow,
                        Uid = folder.NextUid++,
                        ModSeq = ++folder.HighestModSeq,
                        FolderId = folder.Id,
                    };
                    if (!ImapFlagMutation.TryApply(email, ImapFlagMutationMode.Add,
                        message.Flags, out _))
                    {
                        throw new InvalidOperationException("The IMAP APPEND flags changed after validation.");
                    }
                    uids.Add(email.Uid);
                    await content.SetAsync(email, message.RawMessage, cancellationToken).ConfigureAwait(false);
                    await database.Emails.AddAsync(email, cancellationToken).ConfigureAwait(false);
                }

                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                await effects.CommitAsync(marker).ConfigureAwait(false);
            }
            catch
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        ApplicationServiceLog.ImapAppendRollbackFailed(logger, rollbackException);
                    }
                }
                if (commitAttempted)
                    effects.Discard(marker);
                else
                    await effects.RollbackAsync(marker).ConfigureAwait(false);
                throw;
            }

            return new ImapAppendResult(ImapAppendDisposition.Appended,
                folder.UidValidity, uids);
        }
    }

    public async Task<ImapSearchResult> SearchMessagesAsync(
        ImapSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SavedSearchUids is null)
            throw new ArgumentNullException(nameof(request), "The IMAP saved-search UIDs are required.");
        if (request.UserId == Guid.Empty
            || request.FolderId == Guid.Empty
            || request.Criteria is null
            || request.Criteria.Length > 1_048_576
            || request.SavedSearchUids.Any(uid => uid < 1))
        {
            throw new ArgumentException("The IMAP SEARCH request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folderExists = await database.Folders
            .AsNoTracking()
            .AnyAsync(folder => folder.Id == request.FolderId
                && folder.Inbox.OwnerId == request.UserId,
                cancellationToken).ConfigureAwait(false);
            if (!folderExists)
                return new ImapSearchResult(false, null, [], null);

            var search = await ImapSearchEngine.FindSearchCandidatesAsync(
                database.Emails.AsNoTracking().Where(email => email.FolderId == request.FolderId),
                content,
                request.Criteria.Trim(),
                request.SavedSearchUids.ToHashSet(),
                request.Utf8Enabled,
                cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImapSearchResult(
                true,
                search.FailureResponse,
                search.Matches
                    .Select(match => new ImapSearchMatch(match.Uid, match.SequenceNumber))
                    .ToList(),
                search.HighestModSequence);
        }
    }

    public async Task<ImapSortResult> SortMessagesAsync(
        ImapSortRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SavedSearchUids is null)
            throw new ArgumentNullException(nameof(request), "The IMAP saved-search UIDs are required.");
        if (request.SortCriteria is null)
            throw new ArgumentNullException(nameof(request), "The IMAP sort criteria are required.");
        if (request.UserId == Guid.Empty
            || request.FolderId == Guid.Empty
            || request.SearchCriteria is null
            || request.SearchCriteria.Length > 1_048_576
            || request.SavedSearchUids.Any(uid => uid < 1)
            || request.SortCriteria.Count is 0 or > 4096
            || request.SortCriteria.Any(criterion => criterion is null
                || !Enum.IsDefined(criterion.Key))
            || request.Charset is null
            || !request.Charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase)
                && !request.Charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The IMAP SORT request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folderExists = await database.Folders
            .AsNoTracking()
            .AnyAsync(folder => folder.Id == request.FolderId
                && folder.Inbox.OwnerId == request.UserId,
                cancellationToken).ConfigureAwait(false);
            if (!folderExists)
                return new ImapSortResult(false, null, [], null);

            var query = database.Emails
                .AsNoTracking()
                .Where(email => email.FolderId == request.FolderId);
            var search = await ImapSearchEngine.FindSearchCandidatesAsync(
                query,
                content,
                request.SearchCriteria,
                request.SavedSearchUids.ToHashSet(),
                request.Utf8Enabled,
                cancellationToken,
                request.Charset).ConfigureAwait(false);
            if (search.FailureResponse is not null)
                return new ImapSortResult(true, search.FailureResponse, [], null);

            var matchedIds = search.Matches.Select(match => match.Id).ToArray();
            var sequenceById = search.Matches.ToDictionary(
                match => match.Id, match => match.SequenceNumber);
            var stored = await query
                .Where(email => matchedIds.Contains(email.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var messages = new List<ImapSortEngine.SortMessage>(stored.Count);
            foreach (var email in stored)
            {
                var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                var metadata = ImapSortEngine.CreateStoredMessage(
                    email, sequenceById[email.Id], raw);
                messages.Add(ImapSortEngine.CreateSortMessage(metadata));
            }
            messages.Sort((left, right) => ImapSortEngine.CompareSortMessages(
                left, right, request.SortCriteria));
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImapSortResult(
                true,
                null,
                messages.Select(message => new ImapSearchMatch(
                    message.Uid, message.SequenceNumber)).ToList(),
                search.HighestModSequence);
        }
    }

    public async Task<ImapThreadResult> ThreadMessagesAsync(
        ImapThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SavedSearchUids is null)
            throw new ArgumentNullException(nameof(request), "The IMAP saved-search UIDs are required.");
        if (request.UserId == Guid.Empty
            || request.FolderId == Guid.Empty
            || request.SearchCriteria is null
            || request.SearchCriteria.Length > 1_048_576
            || request.SavedSearchUids.Any(uid => uid < 1)
            || !Enum.IsDefined(request.Algorithm)
            || request.Charset is null
            || !request.Charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase)
                && !request.Charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The IMAP THREAD request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folderExists = await database.Folders
            .AsNoTracking()
            .AnyAsync(folder => folder.Id == request.FolderId
                && folder.Inbox.OwnerId == request.UserId,
                cancellationToken).ConfigureAwait(false);
            if (!folderExists)
                return new ImapThreadResult(false, null, []);

            var query = database.Emails
                .AsNoTracking()
                .Where(email => email.FolderId == request.FolderId);
            var search = await ImapSearchEngine.FindSearchCandidatesAsync(
                query,
                content,
                request.SearchCriteria,
                request.SavedSearchUids.ToHashSet(),
                request.Utf8Enabled,
                cancellationToken,
                request.Charset).ConfigureAwait(false);
            if (search.FailureResponse is not null)
                return new ImapThreadResult(true, search.FailureResponse, []);

            var matchedIds = search.Matches.Select(match => match.Id).ToArray();
            var sequenceById = search.Matches.ToDictionary(
                match => match.Id, match => match.SequenceNumber);
            var stored = await query
                .Where(email => matchedIds.Contains(email.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            List<ImapThreadNode> nodes;
            if (request.Algorithm == ImapThreadAlgorithm.OrderedSubject)
            {
                var messages = new List<ImapSortEngine.SortMessage>(stored.Count);
                foreach (var email in stored)
                {
                    var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                    var metadata = ImapSortEngine.CreateStoredMessage(
                        email, sequenceById[email.Id], raw);
                    messages.Add(ImapSortEngine.CreateSortMessage(metadata));
                }
                nodes = ImapThreadEngine.BuildOrderedSubject(messages, request.UseUid);
            }
            else
            {
                var messages = new List<Rfc5256ThreadMessage>(stored.Count);
                foreach (var email in stored)
                {
                    var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                    var metadata = ImapSortEngine.CreateStoredMessage(
                        email, sequenceById[email.Id], raw);
                    messages.Add(ImapThreadEngine.CreateReferenceMessage(
                        metadata, email.MessageId, email.InReplyTo, request.UseUid));
                }
                nodes = Rfc5256Threading.BuildReferencesTree(messages);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImapThreadResult(true, null, nodes);
        }
    }

    public async Task<ImapMarkSeenResult> MarkMessagesSeenAsync(
        ImapMarkSeenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MessageIds is null)
            throw new ArgumentNullException(nameof(request), "The IMAP message identifiers are required.");
        if (request.UserId == Guid.Empty
            || request.FolderId == Guid.Empty
            || request.MessageIds.Count is < 1 or > 256
            || request.MessageIds.Any(id => id == Guid.Empty)
            || request.MessageIds.Distinct().Count() != request.MessageIds.Count)
        {
            throw new ArgumentException("The IMAP seen update is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folder = await database.Folders.FirstOrDefaultAsync(
            candidate => candidate.Id == request.FolderId
                && candidate.Inbox.OwnerId == request.UserId,
            cancellationToken).ConfigureAwait(false);
            if (folder is null)
                return new ImapMarkSeenResult(false, []);

            var metadata = await database.Emails
                .AsNoTracking()
                .Where(email => email.FolderId == folder.Id
                    && request.MessageIds.Contains(email.Id))
                .Select(email => new { email.Id, email.IsRead, email.ModSeq })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var byId = metadata.ToDictionary(email => email.Id);
            var results = new List<ImapSeenMessage>(request.MessageIds.Count);
            var changed = false;
            foreach (var id in request.MessageIds)
            {
                if (!byId.TryGetValue(id, out var email))
                {
                    results.Add(new ImapSeenMessage(id, false, 0));
                    continue;
                }

                var modSeq = email.ModSeq;
                if (!email.IsRead)
                {
                    modSeq = ++folder.HighestModSeq;
                    var update = new EmailDB { Id = id, IsRead = true, ModSeq = modSeq };
                    database.Emails.Attach(update);
                    database.Entry(update).Property(message => message.IsRead).IsModified = true;
                    database.Entry(update).Property(message => message.ModSeq).IsModified = true;
                    changed = true;
                }
                results.Add(new ImapSeenMessage(id, true, modSeq));
            }

            if (changed)
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImapMarkSeenResult(true, results);
        }
    }

    public async Task<ImapFetchPageResult> GetFetchPageAsync(
        ImapFetchPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty
            || request.FolderId == Guid.Empty
            || request.Selection is null
            || !IsValidMessageSelection(request.Selection)
            || request.AfterUid < 0
            || (request.SnapshotMaxUid is null)
                != (request.SnapshotMaximumIdentifier is null)
            || (request.SnapshotMaxUid is null)
                != (request.SnapshotMessageCount is null)
            || request.SnapshotMaxUid is < 0
            || request.SnapshotMaximumIdentifier is < 0
            || request.SnapshotMessageCount is < 0
            || request.SnapshotMaxUid is { } snapshotMaxUid
                && request.AfterUid > snapshotMaxUid
            || request.SnapshotMaxUid is { } maxUidValue
                && request.SnapshotMaximumIdentifier
                    != (request.UseUid ? maxUidValue : request.SnapshotMessageCount))
        {
            throw new ArgumentException("The IMAP FETCH page request is invalid.", nameof(request));
        }

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var folderExists = await database.Folders
            .AsNoTracking()
            .AnyAsync(folder => folder.Id == request.FolderId
                && folder.Inbox.OwnerId == request.UserId,
                cancellationToken).ConfigureAwait(false);
            if (!folderExists)
                return new ImapFetchPageResult(false, 0, 0, 0, request.AfterUid, false, []);

            var query = database.Emails
                .AsNoTracking()
                .Where(email => email.FolderId == request.FolderId);
            var maxUid = request.SnapshotMaxUid
                ?? await query.MaxAsync(email => (int?)email.Uid, cancellationToken).ConfigureAwait(false)
                ?? 0;
            var messageCount = await query.CountAsync(
                email => email.Uid <= maxUid, cancellationToken).ConfigureAwait(false);
            if (request.SnapshotMessageCount is { } snapshotCount
                && messageCount != snapshotCount)
            {
                throw new InvalidOperationException(
                    "The IMAP FETCH mailbox changed during paged enumeration.");
            }
            var maximumIdentifier = request.SnapshotMaximumIdentifier
                ?? (request.UseUid ? maxUid : messageCount);
            var sequenceBefore = request.AfterUid == 0
                ? 0
                : await query.CountAsync(email => email.Uid <= request.AfterUid,
                    cancellationToken).ConfigureAwait(false);
            var candidates = await query
                .Where(email => email.Uid > request.AfterUid && email.Uid <= maxUid)
                .OrderBy(email => email.Uid)
                .Select(email => new { email.Id, email.Uid })
                .Take(FetchScanPageSize + 1)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var resolvedRanges = request.Selection.Ranges is { } ranges
                ? ResolveMessageRanges(ranges.Select(range => (range.Start, range.End)),
                    maximumIdentifier)
                : null;
            var savedSearchUids = request.Selection.SavedSearchUids?.ToHashSet();
            var selected = new List<ImapFetchMessage>();
            var selectedLimit = request.IncludeStoredContent
                ? FetchContentPageSize
                : FetchMetadataPageSize;
            var messageQuery = request.IncludeStoredContent
                ? query
                : query.Select(email => new EmailDB
                {
                    Id = email.Id,
                    Sender = email.Sender,
                    Recipient = email.Recipient,
                    Subject = email.Subject,
                    Body = email.SizeBytes > 0 ? string.Empty : email.Body,
                    IsRead = email.IsRead,
                    IsDeleted = email.IsDeleted,
                    IsFlagged = email.IsFlagged,
                    IsDraft = email.IsDraft,
                    IsAnswered = email.IsAnswered,
                    Keywords = email.Keywords,
                    ModSeq = email.ModSeq,
                    Uid = email.Uid,
                    EmailObjectId = email.EmailObjectId,
                    ThreadObjectId = email.ThreadObjectId,
                    SizeBytes = email.SizeBytes,
                    RawHeaders = email.SizeBytes > 0 ? null : email.RawHeaders,
                    MessageId = email.MessageId,
                    InReplyTo = email.InReplyTo,
                    Cc = email.Cc,
                    ReceivedAt = email.ReceivedAt,
                });
            var candidateIds = candidates.Select(candidate => candidate.Id).ToArray();
            var metadataById = request.IncludeStoredContent
                ? null
                : await messageQuery
                    .Where(email => candidateIds.Contains(email.Id))
                    .ToDictionaryAsync(email => email.Id, cancellationToken).ConfigureAwait(false);
            var contentBytes = 0L;
            var nextAfterUid = request.AfterUid;
            var processed = 0;
            var rangeIndex = 0;
            for (var index = 0; index < candidates.Count && index < FetchScanPageSize; index++)
            {
                if (selected.Count >= selectedLimit)
                    break;

                var candidate = candidates[index];
                var identifier = request.UseUid
                    ? candidate.Uid
                    : sequenceBefore + index + 1;
                var matches = savedSearchUids is null
                    || savedSearchUids.Contains(candidate.Uid);
                if (resolvedRanges is not null)
                {
                    while (rangeIndex < resolvedRanges.Count
                        && resolvedRanges[rangeIndex].End < identifier)
                    {
                        rangeIndex++;
                    }
                    matches = matches && rangeIndex < resolvedRanges.Count
                        && resolvedRanges[rangeIndex].Start <= identifier;
                }

                if (matches)
                {
                    var email = metadataById is not null
                        ? metadataById[candidate.Id]
                        : await messageQuery.SingleAsync(
                            message => message.Id == candidate.Id, cancellationToken).ConfigureAwait(false);
                    var raw = request.IncludeStoredContent
                        ? await content.ReadAsync(email, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (raw is not null
                        && selected.Count > 0
                        && contentBytes + raw.LongLength > FetchContentPageBytes)
                    {
                        break;
                    }
                    if (raw is not null)
                        contentBytes += raw.LongLength;
                    selected.Add(new ImapFetchMessage(
                        email.Id,
                        sequenceBefore + index + 1,
                        email.Uid,
                        email.ModSeq,
                        email.IsRead,
                        email.IsDeleted,
                        email.IsFlagged,
                        email.IsDraft,
                        email.IsAnswered,
                        email.Keywords ?? [],
                        email.ReceivedAt,
                        email.SizeBytes,
                        email.Sender,
                        email.Recipient,
                        email.Cc,
                        email.Subject,
                        request.IncludeStoredContent || email.SizeBytes == 0
                            ? email.Body
                            : string.Empty,
                        request.IncludeStoredContent || email.SizeBytes == 0
                            ? email.RawHeaders
                            : null,
                        email.MessageId,
                        email.InReplyTo,
                        email.EmailObjectId,
                        email.ThreadObjectId,
                        raw));
                }

                nextAfterUid = candidate.Uid;
                processed++;
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImapFetchPageResult(
                true,
                maxUid,
                maximumIdentifier,
                messageCount,
                nextAfterUid,
                processed < candidates.Count,
                selected);
        }
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
                database, request.UserId, request.MailboxName, cancellationToken).ConfigureAwait(false) is null)
        {
            return new ImapQuotaResult(false, 0, 0);
        }

        var quotaBytes = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == request.UserId)
            .Select(user => (long?)user.QuotaBytes)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var usedBytes = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == request.UserId)
            .SumAsync(email => (long?)email.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0;
        return new ImapQuotaResult(true, usedBytes, quotaBytes);
    }

    public async Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
        ImapIdleSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.FolderId == Guid.Empty)
            throw new ArgumentException("The IMAP IDLE snapshot request is invalid.", nameof(request));

        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var highestModSeq = await database.Folders
            .AsNoTracking()
            .Where(folder => folder.Id == request.FolderId
                && folder.Inbox.OwnerId == request.UserId)
            .Select(folder => (long?)folder.HighestModSeq)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
}
