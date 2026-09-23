using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal static class ImapSortEngine
{
    internal sealed record SortStoredMessage(
        Guid Id,
        int Uid,
        int SequenceNumber,
        DateTime ReceivedAt,
        int SizeBytes,
        string Sender,
        string Recipient,
        string? Cc,
        string Subject,
        string? RawHeaders);

    internal sealed record SortMessage(
        int Uid,
        int SequenceNumber,
        DateTime ReceivedAt,
        DateTime SentAt,
        int SizeBytes,
        byte[] FromSortKey,
        byte[] ToSortKey,
        byte[] CcSortKey,
        byte[] SubjectSortKey);

    internal static SortStoredMessage CreateStoredMessage(
        EmailDB email,
        int sequenceNumber,
        byte[] rawMessage)
    {
        var raw = MailWireEncoding.Instance.GetString(rawMessage);
        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0)
            separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = separator >= 0 ? raw[..separator] : raw;
        return new SortStoredMessage(
            email.Id, email.Uid, sequenceNumber, email.ReceivedAt,
            email.SizeBytes, email.Sender, email.Recipient, email.Cc,
            email.Subject, headers);
    }

    internal static SortMessage CreateSortMessage(SortStoredMessage stored)
    {
        var sentAt = stored.ReceivedAt;
        var from = FirstMailboxLocalPart(stored.Sender);
        var to = FirstMailboxLocalPart(stored.Recipient);
        var cc = FirstMailboxLocalPart(stored.Cc);
        var subject = stored.Subject;

        if (!string.IsNullOrEmpty(stored.RawHeaders))
        {
            try
            {
                var rawHeaders = stored.RawHeaders.EndsWith("\r\n\r\n", StringComparison.Ordinal)
                    || stored.RawHeaders.EndsWith("\n\n", StringComparison.Ordinal)
                    ? stored.RawHeaders
                    : stored.RawHeaders + "\r\n\r\n";
                using var stream = new MemoryStream(
                    MailWireEncoding.Instance.GetBytes(rawHeaders),
                    writable: false);
                using var message = MimeMessage.Load(stream, persistent: false);
                from = FirstMailboxLocalPart(message.From);
                to = FirstMailboxLocalPart(message.To);
                cc = FirstMailboxLocalPart(message.Cc);
                subject = message.Subject ?? string.Empty;
                var dateHeader = message.Headers.FirstOrDefault(header =>
                    header.Field.Equals("Date", StringComparison.OrdinalIgnoreCase));
                if (dateHeader is not null
                    && MimeKit.Utils.DateUtils.TryParse(dateHeader.Value, out var parsedDate))
                {
                    sentAt = parsedDate.UtcDateTime;
                }
            }
            catch (Exception exception) when (
                exception is FormatException or IOException or ParseException)
            {
                // Legacy rows can contain malformed raw headers. Their normalized
                // columns remain a deterministic fallback for SORT and THREAD.
            }
        }

        return new SortMessage(
            stored.Uid,
            stored.SequenceNumber,
            stored.ReceivedAt,
            sentAt,
            stored.SizeBytes,
            Rfc5256.UnicodeCasemapSortKey(from),
            Rfc5256.UnicodeCasemapSortKey(to),
            Rfc5256.UnicodeCasemapSortKey(cc),
            Rfc5256.UnicodeCasemapSortKey(Rfc5256.BaseSubject(subject)));
    }

    private static string FirstMailboxLocalPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !InternetAddressList.TryParse(value, out var addresses))
        {
            return string.Empty;
        }
        return FirstMailboxLocalPart(addresses);
    }

    private static string FirstMailboxLocalPart(InternetAddressList addresses)
    {
        var address = addresses.Mailboxes.FirstOrDefault()?.Address;
        if (string.IsNullOrEmpty(address))
            return string.Empty;
        var separator = address.LastIndexOf('@');
        return separator > 0 ? address[..separator] : address;
    }

    internal static int CompareSortMessages(
        SortMessage left,
        SortMessage right,
        IReadOnlyList<ImapSortCriterion> criteria)
    {
        foreach (var criterion in criteria)
        {
            var comparison = criterion.Key switch
            {
                ImapSortKey.Arrival => left.ReceivedAt.CompareTo(right.ReceivedAt),
                ImapSortKey.Cc => left.CcSortKey.AsSpan().SequenceCompareTo(right.CcSortKey),
                ImapSortKey.Date => left.SentAt.CompareTo(right.SentAt),
                ImapSortKey.From => left.FromSortKey.AsSpan().SequenceCompareTo(right.FromSortKey),
                ImapSortKey.Size => left.SizeBytes.CompareTo(right.SizeBytes),
                ImapSortKey.Subject => left.SubjectSortKey.AsSpan().SequenceCompareTo(
                    right.SubjectSortKey),
                ImapSortKey.To => left.ToSortKey.AsSpan().SequenceCompareTo(right.ToSortKey),
                _ => 0,
            };
            if (comparison == 0)
                continue;
            if (!criterion.Reverse)
                return comparison;
            return comparison < 0 ? 1 : -1;
        }

        return left.SequenceNumber.CompareTo(right.SequenceNumber);
    }
}
