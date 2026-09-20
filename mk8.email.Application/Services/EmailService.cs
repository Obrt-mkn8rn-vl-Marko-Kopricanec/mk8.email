using System.Data;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class EmailService(EmailDbContext db) : IEmailService
{
    public async Task<bool> CanReceiveAsync(
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetInboxAsync(
            recipient,
            allowCatchAll: true,
            cancellationToken);
        return target is not null;
    }

    public async Task<bool> DeliverAsync(
        string sender,
        string recipient,
        string rawMessage,
        string folderName = DefaultFolders.Inbox,
        Guid? queueDeliveryId = null,
        CancellationToken cancellationToken = default)
    {
        if (!DefaultFolders.All.Contains(folderName, StringComparer.Ordinal))
            throw new ArgumentException("The delivery folder is not valid.", nameof(folderName));

        if (queueDeliveryId is not null
            && await db.Emails.AsNoTracking().AnyAsync(
                message => message.QueueDeliveryId == queueDeliveryId,
                cancellationToken))
        {
            return true;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var target = await ResolveTargetInboxAsync(
            recipient,
            allowCatchAll: true,
            cancellationToken);
        if (target is null)
            return false;

        var messageSize = MailWireEncoding.Instance.GetByteCount(rawMessage);
        if (!await HasQuotaCapacityAsync(target, messageSize, cancellationToken))
            return false;

        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.InboxId == target.Id
                                   && f.Name == folderName,
                cancellationToken);
        if (folder is null)
            return false;

        var uid = folder.NextUid++;
        var modSeq = ++folder.HighestModSeq;

        var (subject, body, headers) = ParseMessage(rawMessage);

        var messageId = MailMessageParser.ExtractHeaderValue(headers, "Message-ID");
        if (string.IsNullOrEmpty(messageId))
            messageId = $"<{Guid.NewGuid()}@{target.Domain}>";

        var inReplyTo = MailMessageParser.ExtractHeaderValue(headers, "In-Reply-To");
        var threadId = await ResolveThreadObjectIdAsync(inReplyTo, messageId, cancellationToken);

        db.Emails.Add(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Sender = sender,
            Recipient = recipient,
            Subject = subject.Length > 998 ? subject[..998] : subject,
            Body = body,
            RawHeaders = headers,
            RawMessage = MailWireEncoding.Instance.GetBytes(rawMessage),
            SizeBytes = messageSize,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            Cc = MailMessageParser.ExtractHeaderValue(headers, "Cc"),
            EmailObjectId = Guid.CreateVersion7().ToString("N"),
            ThreadObjectId = threadId,
            QueueDeliveryId = queueDeliveryId,
            Uid = uid,
            ModSeq = modSeq,
            FolderId = folder.Id,
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
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
                cancellationToken))
        {
            return true;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var target = await ResolveTargetInboxAsync(
            sender,
            allowCatchAll: false,
            cancellationToken);
        if (target is null)
            return false;

        var messageSize = MailWireEncoding.Instance.GetByteCount(rawMessage);
        if (!await HasQuotaCapacityAsync(target, messageSize, cancellationToken))
            return false;

        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.InboxId == target.Id
                                   && f.Name == DefaultFolders.Sent,
                cancellationToken);
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
            sentInReplyTo,
            sentMessageId,
            cancellationToken);

        db.Emails.Add(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Sender = sender,
            Recipient = recipient,
            Subject = subject.Length > 998 ? subject[..998] : subject,
            Body = body,
            RawHeaders = headers,
            RawMessage = MailWireEncoding.Instance.GetBytes(rawMessage),
            SizeBytes = messageSize,
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
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static (string? localPart, string? domain) ParseRecipient(string address)
    {
        var parts = address.Split('@', 2);
        return parts.Length == 2
            ? (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant())
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
            .FirstOrDefaultAsync(cancellationToken);
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
            .SingleOrDefaultAsync(cancellationToken);
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
            ?? 0;
        return usedBytes < target.QuotaBytes
            && addedBytes <= target.QuotaBytes - usedBytes;
    }

    private async Task<string> ResolveThreadObjectIdAsync(
        string? inReplyTo,
        string? messageId,
        CancellationToken cancellationToken)
    {
        // If this message is a reply, try to find the thread of the parent message
        if (!string.IsNullOrEmpty(inReplyTo))
        {
            var parent = await db.Emails.AsNoTracking()
                .FirstOrDefaultAsync(e => e.MessageId == inReplyTo, cancellationToken);
            if (parent?.ThreadObjectId is not null)
                return parent.ThreadObjectId;
        }

        // Check if any existing message references this one (forward-thread linking)
        if (!string.IsNullOrEmpty(messageId))
        {
            var child = await db.Emails.AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.InReplyTo == messageId && e.ThreadObjectId != null,
                    cancellationToken);
            if (child?.ThreadObjectId is not null)
                return child.ThreadObjectId;
        }

        // New thread
        return Guid.CreateVersion7().ToString("N");
    }

    private static ParsedMailMessage ParseMessage(string rawMessage) =>
        MailMessageParser.Parse(rawMessage);

    private sealed record TargetInbox(Guid Id, string Domain, Guid OwnerId, long QuotaBytes);

}
