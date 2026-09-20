using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapStoredEmailResult(EmailDB? Email, JsonObject? Error);

internal sealed class JmapEmailStore(
    EmailDbContext database,
    EnvironmentConfig environment)
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
            return new JmapStoredEmailResult(null, JmapMethodHelpers.SetError("tooLarge"));
        var used = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == account.UserId)
            .SumAsync(email => (long?)email.SizeBytes, cancellationToken)
            ?? 0;
        if (account.QuotaBytes > 0
            && (used >= account.QuotaBytes || raw.LongLength > account.QuotaBytes - used))
        {
            return new JmapStoredEmailResult(null, JmapMethodHelpers.SetError("overQuota"));
        }

        MimeMessage message;
        try
        {
            message = JmapEmailCodec.Parse(raw);
        }
        catch (FormatException)
        {
            return new JmapStoredEmailResult(null, JmapMethodHelpers.SetError("invalidEmail"));
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
                cancellationToken);
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
            var email = new EmailDB
            {
                Id = id,
                Sender = sender.Length > 255 ? sender[..255] : sender,
                Recipient = recipient,
                Cc = string.IsNullOrEmpty(cc) ? null : cc,
                Subject = subject,
                Body = body,
                RawHeaders = headers,
                RawMessage = raw.ToArray(),
                SizeBytes = raw.Length,
                MessageId = messageId,
                InReplyTo = inReplyTo,
                EmailObjectId = id.ToString("N"),
                ThreadObjectId = threadId,
                ReceivedAt = receivedAt.ToUniversalTime(),
                FolderId = folder.Id,
                Uid = folder.NextUid++,
                ModSeq = ++folder.HighestModSeq,
            };
            ApplyKeywords(email, keywords);
            database.Emails.Add(email);
            await database.SaveChangesAsync(cancellationToken);
            return new JmapStoredEmailResult(email, null);
        }
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

    public static bool TryParseKeywords(
        JsonNode? node,
        out IReadOnlySet<string> keywords,
        out string? error)
    {
        keywords = new HashSet<string>(StringComparer.Ordinal);
        error = null;
        if (node is null)
            return true;
        if (node is not JsonObject map)
            return false;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in map)
        {
            if (item.Value is not JsonValue value
                || !value.TryGetValue<bool>(out var enabled)
                || !enabled)
            {
                return false;
            }
            var normalized = item.Key.ToLowerInvariant();
            if (!JmapEmailCodec.IsValidKeyword(normalized))
                return false;
            result.Add(normalized);
        }
        if (result.Count > 128)
        {
            error = "tooManyKeywords";
            return false;
        }
        keywords = result;
        return true;
    }

    public static bool TryParseReceivedAt(JsonNode? node, out DateTime value)
    {
        value = DateTime.UtcNow;
        if (node is null)
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text is null
            || !JmapDate.TryParseUtcDate(text, out var parsed))
        {
            return false;
        }
        value = parsed.UtcDateTime;
        return true;
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
                    cancellationToken);
            if (parent?.ThreadObjectId is not null)
                return parent.ThreadObjectId;
        }
        var child = await database.Emails
            .AsNoTracking()
            .FirstOrDefaultAsync(email => email.Folder.InboxId == accountId
                && email.InReplyTo == messageId
                && email.ThreadObjectId != null,
                cancellationToken);
        return child?.ThreadObjectId ?? Guid.CreateVersion7().ToString("N");
    }
}
