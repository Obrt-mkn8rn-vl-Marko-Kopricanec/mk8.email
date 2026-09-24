using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Imap;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal static class ImapThreadEngine
{
    internal static List<ImapThreadNode> BuildOrderedSubject(
        List<ImapSortEngine.SortMessage> messages,
        bool useUid)
    {
        messages.Sort(static (left, right) =>
        {
            var subjectComparison = left.SubjectSortKey.AsSpan().SequenceCompareTo(
                right.SubjectSortKey);
            return subjectComparison != 0
                ? subjectComparison
                : CompareSentDate(left, right);
        });

        var groups = new List<List<ImapSortEngine.SortMessage>>();
        for (var index = 0; index < messages.Count;)
        {
            var group = new List<ImapSortEngine.SortMessage> { messages[index++] };
            while (index < messages.Count
                   && group[0].SubjectSortKey.AsSpan().SequenceEqual(
                       messages[index].SubjectSortKey))
            {
                group.Add(messages[index++]);
            }
            groups.Add(group);
        }
        groups.Sort(static (left, right) => CompareSentDate(left[0], right[0]));

        var result = new List<ImapThreadNode>(messages.Count);
        foreach (var group in groups)
        {
            var rootIndex = result.Count;
            result.Add(new ImapThreadNode(Identifier(group[0], useUid), -1));
            for (var index = 1; index < group.Count; index++)
                result.Add(new ImapThreadNode(Identifier(group[index], useUid), rootIndex));
        }
        return result;
    }

    private static int CompareSentDate(
        ImapSortEngine.SortMessage left,
        ImapSortEngine.SortMessage right)
    {
        var dateComparison = left.SentAt.CompareTo(right.SentAt);
        return dateComparison != 0
            ? dateComparison
            : left.SequenceNumber.CompareTo(right.SequenceNumber);
    }

    private static int Identifier(ImapSortEngine.SortMessage message, bool useUid) =>
        useUid ? message.Uid : message.SequenceNumber;

    internal static Rfc5256ThreadMessage CreateReferenceMessage(
        ImapSortEngine.SortStoredMessage stored,
        string? storedMessageId,
        string? storedInReplyTo,
        bool useUid)
    {
        var sentAt = stored.ReceivedAt;
        var subject = stored.Subject;
        var messageId = Rfc5256Threading.ParseFirstMessageId(storedMessageId);
        IReadOnlyList<string> references = Rfc5256Threading.ParseMessageIds(storedInReplyTo)
            .Take(1)
            .ToArray();

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
                subject = message.Subject ?? string.Empty;
                var dateHeader = message.Headers.FirstOrDefault(header =>
                    header.Field.Equals("Date", StringComparison.OrdinalIgnoreCase));
                if (dateHeader is not null
                    && MimeKit.Utils.DateUtils.TryParse(dateHeader.Value, out var parsedDate))
                {
                    sentAt = parsedDate.UtcDateTime;
                }

                var messageIds = MessageIdsFromHeaders(message, "Message-ID");
                messageId = messageIds.Count > 0 ? messageIds[0] : null;
                var headerReferences = MessageIdsFromHeaders(message, "References");
                references = headerReferences.Count > 0
                    ? headerReferences
                    : MessageIdsFromHeaders(message, "In-Reply-To")
                        .Take(1)
                        .ToArray();
            }
            catch (Exception exception) when (
                exception is FormatException or IOException or ParseException)
            {
                // Preserve deterministic normalized-column fallback for legacy rows.
            }
        }

        var analyzedSubject = Rfc5256.AnalyzeSubject(subject);
        return new Rfc5256ThreadMessage(
            useUid ? stored.Uid : stored.SequenceNumber,
            stored.SequenceNumber,
            sentAt,
            Convert.ToBase64String(
                Rfc5256.UnicodeCasemapSortKey(analyzedSubject.BaseSubject)),
            analyzedSubject.IsReplyOrForward,
            messageId,
            references);
    }

    private static List<string> MessageIdsFromHeaders(
        MimeMessage message,
        string fieldName)
    {
        var result = new List<string>();
        foreach (var header in message.Headers)
        {
            if (header.Field.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                result.AddRange(Rfc5256Threading.ParseMessageIds(header.Value));
        }
        return result;
    }
}
