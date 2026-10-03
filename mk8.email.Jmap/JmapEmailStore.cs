using System.Globalization;
using System.Text;
using mk8.email.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class JmapEmailStore(
    EmailDbContext database,
    EnvironmentConfig environment,
    MailboxMessageContentService content)
{
    public async Task<JmapStoredEmailResult> StoreAsync(
        JmapAccount account,
        FolderDB folder,
        byte[] raw,
        IReadOnlySet<string> keywords,
        DateTime receivedAt,
        CancellationToken cancellationToken)
    {
        if (raw.LongLength > environment.Limits.MaxMessageSizeBytes)
            return new JmapStoredEmailResult(null, new(MailMessageMutationError.TooLarge, null, null, null));
        var used = await GetUsedStorageAsync(account.UserId, cancellationToken).ConfigureAwait(false);
        if (account.QuotaBytes > 0
            && (used >= account.QuotaBytes || raw.LongLength > account.QuotaBytes - used))
        {
            return new JmapStoredEmailResult(null, new(MailMessageMutationError.OverQuota, null, null, null));
        }

        MimeMessage message;
        try
        {
            message = JmapEmailCodec.Parse(raw);
        }
        catch (FormatException)
        {
            return new JmapStoredEmailResult(null, new(MailMessageMutationError.InvalidEmail, null, null, null));
        }
        using (message)
        {
            var messageId = string.IsNullOrWhiteSpace(message.MessageId)
                ? $"<{Guid.NewGuid():N}@{account.Address[(account.Address.LastIndexOf('@') + 1)..]}>"
                : $"<{message.MessageId.Trim('<', '>')}>";
            var inReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo)
                ? null
                : $"<{message.InReplyTo.Trim('<', '>')}>";
            var threadId = await ResolveThreadIdAsync(
                account.InboxId,
                inReplyTo,
                messageId,
                cancellationToken).ConfigureAwait(false);
            var email = CreateRow(account, folder, message, raw, receivedAt, messageId, inReplyTo, threadId);
            ApplyKeywords(email, keywords);
            await content.SetAsync(email, raw, cancellationToken).ConfigureAwait(false);
            await database.Emails.AddAsync(email, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new JmapStoredEmailResult(email, null);
        }
    }

    private static EmailDB CreateRow(JmapAccount account, FolderDB folder, MimeMessage message, byte[] raw,
        DateTime receivedAt, string messageId, string? inReplyTo, string threadId)
    {
        var rawText = Encoding.Latin1.GetString(raw);
        var separator = rawText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separator < 0)
        {
            separator = rawText.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }
        var headers = separator < 0 ? rawText : rawText[..separator];
        var body = separator < 0 ? string.Empty : rawText[(separator + separatorLength)..];
        var id = Guid.CreateVersion7();
        var sender = message.From.Mailboxes.FirstOrDefault()?.Address ?? account.Address;
        var recipient = string.Join(", ", message.To.Mailboxes.Select(mailbox => mailbox.Address));
        if (recipient.Length > 255) recipient = recipient[..255];
        var cc = message.Cc.ToString();
        if (cc.Length > 255) cc = cc[..255];
        var subject = message.Subject ?? string.Empty;
        if (subject.Length > 998) subject = subject[..998];
        return new EmailDB
        {
            Id = id,
            Sender = sender.Length > 255 ? sender[..255] : sender,
            Recipient = recipient,
            Cc = string.IsNullOrEmpty(cc) ? null : cc,
            Subject = subject,
            Body = body,
            RawHeaders = headers,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            EmailObjectId = id.ToString("N"),
            ThreadObjectId = threadId,
            ReceivedAt = receivedAt.ToUniversalTime(),
            FolderId = folder.Id,
            Uid = folder.NextUid++,
            ModSeq = ++folder.HighestModSeq,
        };
    }

    public static void ApplyKeywords(EmailDB email, IReadOnlySet<string> keywords)
    {
        email.IsRead = keywords.Contains("$seen");
        email.IsFlagged = keywords.Contains("$flagged");
        email.IsDraft = keywords.Contains("$draft");
        email.IsAnswered = keywords.Contains("$answered");
        email.Keywords = keywords
            .Where(keyword => keyword is not ("$seen" or "$flagged" or "$draft" or "$answered"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<long> GetUsedStorageAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var knownSize = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == userId && email.SizeBytes > 0)
            .SumAsync(email => (long?)email.SizeBytes, cancellationToken).ConfigureAwait(false)
            ?? 0;
        var unknownSize = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == userId && email.SizeBytes <= 0)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var used = knownSize;
        for (var emailIndex = 0; emailIndex < unknownSize.Count; emailIndex++)
        {
            var email = unknownSize[emailIndex];
            var size = (await content.ReadAsync(email, cancellationToken).ConfigureAwait(false)).LongLength;
            used = size > long.MaxValue - used ? long.MaxValue : used + size;
        }
        return used;
    }

    private async Task<string> ResolveThreadIdAsync(
        Guid accountId,
        string? inReplyTo,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (inReplyTo is not null)
        {
            var parent = await database.Emails
                .AsNoTracking()
                .FirstOrDefaultAsync(email => email.Folder.InboxId == accountId
                    && email.MessageId == inReplyTo
                    && email.ThreadObjectId != null,
                    cancellationToken).ConfigureAwait(false);
            if (parent?.ThreadObjectId is not null)
                return parent.ThreadObjectId;
        }
        var child = await database.Emails
            .AsNoTracking()
            .FirstOrDefaultAsync(email => email.Folder.InboxId == accountId
                && email.InReplyTo == messageId
                && email.ThreadObjectId != null,
                cancellationToken).ConfigureAwait(false);
        return child?.ThreadObjectId ?? Guid.CreateVersion7().ToString("N");
    }
}
