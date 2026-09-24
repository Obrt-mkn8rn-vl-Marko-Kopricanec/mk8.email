using System.Data;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class EmailService(
    EmailDbContext db,
    MailboxMessageContentService content,
    LargeObjectTransactionEffects transactionEffects) : IEmailService
{
    public async Task<bool> CanReceiveAsync(
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetInboxAsync(
            recipient,
            allowCatchAll: true,
            cancellationToken).ConfigureAwait(false);
        return target is not null;
    }

    public async Task<bool> DeliverAsync(
        string sender,
        string recipient,
        string rawMessage,
        string folderName = DefaultFolders.Inbox,
        Guid? queueDeliveryId = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? flags = null,
        bool createFolder = false)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        folderName = MailboxName.Normalize(folderName);
        if (!MailboxName.IsValid(folderName))
            throw new ArgumentException("The delivery folder is not valid.", nameof(folderName));
        if (!TryNormalizeFlags(flags, out var normalizedFlags))
            throw new ArgumentException("The delivery flags are not valid.", nameof(flags));

        if (queueDeliveryId is not null
            && await db.Emails.AsNoTracking().AnyAsync(
                message => message.QueueDeliveryId == queueDeliveryId,
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var target = await ResolveTargetInboxAsync(
            recipient,
            allowCatchAll: true,
            cancellationToken).ConfigureAwait(false);
            if (target is null)
                return false;

            var messageSize = MailWireEncoding.Instance.GetByteCount(rawMessage);
            if (!await HasQuotaCapacityAsync(target, messageSize, cancellationToken).ConfigureAwait(false))
                return false;

            var folder = await db.Folders
                .FirstOrDefaultAsync(f => f.InboxId == target.Id
                                       && f.Name == folderName,
                    cancellationToken).ConfigureAwait(false);
            if (folder is null)
            {
                if (!createFolder)
                    return false;
                folder = new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    InboxId = target.Id,
                    Name = folderName,
                };
                await db.Folders.AddAsync(folder, cancellationToken).ConfigureAwait(false);
            }

            var uid = folder.NextUid++;
            var modSeq = ++folder.HighestModSeq;

            var (subject, body, headers) = ParseMessage(rawMessage);

            var messageId = MailMessageParser.ExtractHeaderValue(headers, "Message-ID");
            if (string.IsNullOrEmpty(messageId))
                messageId = $"<{Guid.NewGuid()}@{target.Domain}>";

            var inReplyTo = MailMessageParser.ExtractHeaderValue(headers, "In-Reply-To");
            var threadId = await ResolveThreadObjectIdAsync(
                target.Id,
                inReplyTo,
                messageId,
                cancellationToken).ConfigureAwait(false);

            var rawBytes = MailWireEncoding.Instance.GetBytes(rawMessage);
            var email = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = sender,
                Recipient = recipient,
                Subject = subject.Length > 998 ? subject[..998] : subject,
                Body = body,
                RawHeaders = headers,
                MessageId = messageId,
                InReplyTo = inReplyTo,
                Cc = MailMessageParser.ExtractHeaderValue(headers, "Cc"),
                EmailObjectId = Guid.CreateVersion7().ToString("N"),
                ThreadObjectId = threadId,
                QueueDeliveryId = queueDeliveryId,
                Uid = uid,
                ModSeq = modSeq,
                FolderId = folder.Id,
            };
            ApplyFlags(email, normalizedFlags);
            var marker = transactionEffects.Mark();
            var commitAttempted = false;
            try
            {
                await content.SetAsync(email, rawBytes, cancellationToken).ConfigureAwait(false);
                await db.Emails.AddAsync(email, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await transactionEffects.CommitAsync(marker).ConfigureAwait(false);
            }
            catch
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                await CompleteRollbackAsync(marker, commitAttempted).ConfigureAwait(false);
                throw;
            }
            return true;
        }
    }

    public async Task<bool> SaveSentCopyAsync(
        string sender,
        string rawMessage,
        Guid? queueDeliveryId = null,
        CancellationToken cancellationToken = default)
    {
        if (queueDeliveryId is not null
            && await db.Emails.AsNoTracking().AnyAsync(
                message => message.QueueDeliveryId == queueDeliveryId,
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var target = await ResolveTargetInboxAsync(
            sender,
            allowCatchAll: false,
            cancellationToken).ConfigureAwait(false);
            if (target is null)
                return false;

            var messageSize = MailWireEncoding.Instance.GetByteCount(rawMessage);
            if (!await HasQuotaCapacityAsync(target, messageSize, cancellationToken).ConfigureAwait(false))
                return false;

            var folder = await db.Folders
                .FirstOrDefaultAsync(f => f.InboxId == target.Id
                                       && f.Name == DefaultFolders.Sent,
                    cancellationToken).ConfigureAwait(false);
            if (folder is null)
                return false;

            var uid = folder.NextUid++;
            var modSeq = ++folder.HighestModSeq;

            var (subject, body, headers) = ParseMessage(rawMessage);
            var recipient = MailMessageParser.ExtractHeaderValue(headers, "To");

            var sentMessageId = MailMessageParser.ExtractHeaderValue(headers, "Message-ID");
            if (string.IsNullOrEmpty(sentMessageId))
                sentMessageId = $"<{Guid.NewGuid()}@{target.Domain}>";

            var sentInReplyTo = MailMessageParser.ExtractHeaderValue(headers, "In-Reply-To");
            var sentThreadId = await ResolveThreadObjectIdAsync(
                target.Id,
                sentInReplyTo,
                sentMessageId,
                cancellationToken).ConfigureAwait(false);

            var rawBytes = MailWireEncoding.Instance.GetBytes(rawMessage);
            var email = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = sender,
                Recipient = recipient,
                Subject = subject.Length > 998 ? subject[..998] : subject,
                Body = body,
                RawHeaders = headers,
                MessageId = sentMessageId,
                InReplyTo = sentInReplyTo,
                Cc = MailMessageParser.ExtractHeaderValue(headers, "Cc"),
                EmailObjectId = Guid.CreateVersion7().ToString("N"),
                ThreadObjectId = sentThreadId,
                QueueDeliveryId = queueDeliveryId,
                Uid = uid,
                ModSeq = modSeq,
                IsRead = true,
                FolderId = folder.Id,
            };
            var marker = transactionEffects.Mark();
            var commitAttempted = false;
            try
            {
                await content.SetAsync(email, rawBytes, cancellationToken).ConfigureAwait(false);
                await db.Emails.AddAsync(email, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await transactionEffects.CommitAsync(marker).ConfigureAwait(false);
            }
            catch
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                await CompleteRollbackAsync(marker, commitAttempted).ConfigureAwait(false);
                throw;
            }
            return true;
        }
    }

    private async Task CompleteRollbackAsync(int marker, bool commitAttempted)
    {
        if (commitAttempted)
        {
            transactionEffects.Discard(marker);
            return;
        }
        await transactionEffects.RollbackAsync(marker).ConfigureAwait(false);
    }

    private static async Task TryRollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original failure; an ambiguous commit retains the object.
        }
    }

    private static (string? localPart, string? domain) ParseRecipient(string address)
    {
        var parts = address.Split('@', 2);
        return parts.Length == 2
            ? (parts[0].ToMailLowerInvariant(), parts[1].ToMailLowerInvariant())
            : (null, null);
    }

    private async Task<TargetInbox?> ResolveTargetInboxAsync(
        string address,
        bool allowCatchAll,
        CancellationToken cancellationToken)
    {
        var (localPart, domain) = ParseRecipient(address);
        if (localPart is null || domain is null)
            return null;

        var route = await db.Inboxes
            .AsNoTracking()
            .Where(inbox => (inbox.Name == localPart
                    || allowCatchAll && inbox.Name == "*")
                && inbox.Address.Domain == domain
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive
                && (inbox.Name != "*" || inbox.AliasForInboxId != null))
            .OrderBy(inbox => inbox.Name == localPart ? 0 : 1)
            .Select(inbox => new
            {
                inbox.Id,
                inbox.AliasForInboxId,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (route is null)
            return null;

        var targetId = route.AliasForInboxId ?? route.Id;
        return await db.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.Id == targetId
                && inbox.Owner.IsActive
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive)
            .Select(inbox => new TargetInbox(
                inbox.Id,
                inbox.Address.Domain,
                inbox.OwnerId,
                inbox.Owner.QuotaBytes))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasQuotaCapacityAsync(
        TargetInbox target,
        int addedBytes,
        CancellationToken cancellationToken)
    {
        if (target.QuotaBytes <= 0)
            return true;

        var usedBytes = await db.Emails
            .AsNoTracking()
            .Where(message => message.Folder.Inbox.OwnerId == target.OwnerId)
            .SumAsync(message => (long?)message.SizeBytes, cancellationToken)
.ConfigureAwait(false) ?? 0;
        return usedBytes < target.QuotaBytes
            && addedBytes <= target.QuotaBytes - usedBytes;
    }

    private async Task<string> ResolveThreadObjectIdAsync(
        Guid inboxId,
        string? inReplyTo,
        string? messageId,
        CancellationToken cancellationToken)
    {
        // If this message is a reply, try to find the thread of the parent message
        if (!string.IsNullOrEmpty(inReplyTo))
        {
            var parent = await db.Emails.AsNoTracking()
                .FirstOrDefaultAsync(
                    email => email.Folder.InboxId == inboxId
                        && email.MessageId == inReplyTo,
                    cancellationToken).ConfigureAwait(false);
            if (parent?.ThreadObjectId is not null)
                return parent.ThreadObjectId;
        }

        // Check if any existing message references this one (forward-thread linking)
        if (!string.IsNullOrEmpty(messageId))
        {
            var child = await db.Emails.AsNoTracking()
                .FirstOrDefaultAsync(
                    email => email.Folder.InboxId == inboxId
                        && email.InReplyTo == messageId
                        && email.ThreadObjectId != null,
                    cancellationToken).ConfigureAwait(false);
            if (child?.ThreadObjectId is not null)
                return child.ThreadObjectId;
        }

        // New thread
        return Guid.CreateVersion7().ToString("N");
    }

    private static ParsedMailMessage ParseMessage(string rawMessage) =>
        MailMessageParser.Parse(rawMessage);

    private static bool TryNormalizeFlags(
        IReadOnlyCollection<string>? flags,
        out IReadOnlyList<string> normalized)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in flags ?? [])
        {
            if (!IsValidFlag(flag))
            {
                normalized = [];
                return false;
            }
            values.Add(flag);
        }
        if (values.Count(item => !item.StartsWith('\\')) > 128)
        {
            normalized = [];
            return false;
        }
        normalized = values
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(flag => flag, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool IsValidFlag(string flag)
    {
        if (flag.Length is < 1 or > 255)
            return false;
        if (flag[0] == '\\')
            return flag.ToUpperInvariant() is "\\SEEN" or "\\DELETED" or "\\FLAGGED" or "\\DRAFT" or "\\ANSWERED";
        return flag.All(character => character > ' '
            && character < '\u007f'
            && character is not '(' and not ')' and not '{' and not '%' and not '*' and not ']');
    }

    private static void ApplyFlags(EmailDB email, IReadOnlyList<string> flags)
    {
        var keywords = new List<string>();
        foreach (var flag in flags)
        {
            switch (flag.ToUpperInvariant())
            {
                case "\\SEEN": email.IsRead = true; break;
                case "\\DELETED": email.IsDeleted = true; break;
                case "\\FLAGGED": email.IsFlagged = true; break;
                case "\\DRAFT": email.IsDraft = true; break;
                case "\\ANSWERED": email.IsAnswered = true; break;
                default: keywords.Add(flag); break;
            }
        }
        email.Keywords = keywords.ToArray();
    }

    private sealed record TargetInbox(Guid Id, string Domain, Guid OwnerId, long QuotaBytes);

}
