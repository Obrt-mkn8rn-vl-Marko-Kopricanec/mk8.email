using System.Text;
using MimeKit;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailSubmissionEmailValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ParserOptions StrictAddressParserOptions = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false,
        AllowUnquotedCommasInAddresses = false,
    };
    private static readonly Dictionary<string, MailSubmissionEmailIssue> SingletonHeaders =
        new Dictionary<string, MailSubmissionEmailIssue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = MailSubmissionEmailIssue.HeaderDate,
            ["From"] = MailSubmissionEmailIssue.From,
            ["Sender"] = MailSubmissionEmailIssue.Sender,
            ["Reply-To"] = MailSubmissionEmailIssue.ReplyTo,
            ["To"] = MailSubmissionEmailIssue.To,
            ["Cc"] = MailSubmissionEmailIssue.Cc,
            ["Bcc"] = MailSubmissionEmailIssue.Bcc,
            ["Message-ID"] = MailSubmissionEmailIssue.MessageId,
            ["In-Reply-To"] = MailSubmissionEmailIssue.InReplyTo,
            ["References"] = MailSubmissionEmailIssue.References,
            ["Subject"] = MailSubmissionEmailIssue.Subject,
        };
    private static readonly HashSet<string> SingletonMimeHeaders = new HashSet<string>(
        [
            "MIME-Version", "Content-Type", "Content-Transfer-Encoding",
            "Content-Disposition", "Content-ID", "Content-Description",
            "Content-Language", "Content-Location",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MailSubmissionEmailIssue> ResentHeaderProperties =
        new Dictionary<string, MailSubmissionEmailIssue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Resent-Date"] = MailSubmissionEmailIssue.ResentDate,
            ["Resent-From"] = MailSubmissionEmailIssue.ResentFrom,
            ["Resent-Sender"] = MailSubmissionEmailIssue.ResentSender,
            ["Resent-To"] = MailSubmissionEmailIssue.ResentTo,
            ["Resent-Cc"] = MailSubmissionEmailIssue.ResentCc,
            ["Resent-Bcc"] = MailSubmissionEmailIssue.ResentBcc,
            ["Resent-Message-ID"] = MailSubmissionEmailIssue.ResentMessageId,
        };

    public static bool TryValidate(
        byte[] raw,
        out IReadOnlyList<MailSubmissionEmailIssue> invalidProperties)
    {
        var invalid = new HashSet<MailSubmissionEmailIssue>();
        invalidProperties = [];
        ValidateWireFormat(raw, invalid);
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            ValidateMessage(message, invalid);
        }
        catch (FormatException)
        {
            invalid.Add(MailSubmissionEmailIssue.BodyTree);
        }

        invalidProperties = invalid.Order().ToArray();
        return invalidProperties.Count == 0;
    }

    private static void ValidateMessage(MimeMessage message, HashSet<MailSubmissionEmailIssue> invalid)
    {
        foreach (var singleton in SingletonHeaders)
        {
            var count = message.Headers.Count(header => header.Field.Equals(
                singleton.Key,
                StringComparison.OrdinalIgnoreCase));
            if (count > 1)
                invalid.Add(singleton.Value);
        }

        var dateHeaders = Headers(message.Headers, "Date");
        if (dateHeaders.Length != 1
            || !JmapDate.IsValidRfc5322DateTime(dateHeaders[0].Value))
        {
            invalid.Add(MailSubmissionEmailIssue.HeaderDate);
        }

        ValidateOrigin(message.Headers, invalid);

        ValidateAddressHeader(message.Headers, "Reply-To", MailSubmissionEmailIssue.ReplyTo, false, invalid);
        ValidateAddressHeader(message.Headers, "To", MailSubmissionEmailIssue.To, false, invalid);
        ValidateAddressHeader(message.Headers, "Cc", MailSubmissionEmailIssue.Cc, false, invalid);
        ValidateAddressHeader(message.Headers, "Bcc", MailSubmissionEmailIssue.Bcc, true, invalid);
        ValidateMessageIdsHeader(
            message.Headers,
            "Message-ID",
            MailSubmissionEmailIssue.MessageId,
            requireSingle: true,
            invalid);
        ValidateMessageIdsHeader(
            message.Headers,
            "In-Reply-To",
            MailSubmissionEmailIssue.InReplyTo,
            requireSingle: false,
            invalid);
        ValidateMessageIdsHeader(
            message.Headers,
            "References",
            MailSubmissionEmailIssue.References,
            requireSingle: false,
            invalid);
        ValidateResentHeaders(message.Headers, invalid);

        if (message.Headers
            .Where(header => SingletonMimeHeaders.Contains(header.Field))
            .GroupBy(header => header.Field, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1)
            || HasDuplicateMimeHeaders(message.Body))
        {
            invalid.Add(MailSubmissionEmailIssue.BodyTree);
        }
    }

    private static void ValidateOrigin(HeaderList headers, HashSet<MailSubmissionEmailIssue> invalid)
    {
        var fromHeaders = Headers(headers, "From");
        InternetAddressList? from = null;
        if (fromHeaders.Length != 1
            || !InternetAddressList.TryParse(
                StrictAddressParserOptions,
                fromHeaders[0].Value,
                out from)
            || from.Count == 0
            || from.Any(address => address is not MailboxAddress))
        {
            invalid.Add(MailSubmissionEmailIssue.From);
        }

        var senderHeaders = Headers(headers, "Sender");
        if (senderHeaders.Length == 1
            && !MailboxAddress.TryParse(
                StrictAddressParserOptions,
                senderHeaders[0].Value,
                out _))
        {
            invalid.Add(MailSubmissionEmailIssue.Sender);
        }
        if (from is not null && from.Mailboxes.Skip(1).Any() && senderHeaders.Length != 1)
            invalid.Add(MailSubmissionEmailIssue.Sender);

    }

    private static void ValidateWireFormat(byte[] raw, HashSet<MailSubmissionEmailIssue> invalid)
    {
        var inHeaders = true;
        var hasHeader = false;
        var lineStart = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] == (byte)'\r')
            {
                if (index + 1 >= raw.Length || raw[index + 1] != (byte)'\n')
                {
                    invalid.Add(inHeaders ? MailSubmissionEmailIssue.RawHeaders : MailSubmissionEmailIssue.BodyTree);
                    continue;
                }

                var line = raw.AsSpan(lineStart, index - lineStart);
                ValidateWireLine(line, inHeaders, ref hasHeader, invalid);
                if (inHeaders && line.Length == 0)
                    inHeaders = false;
                index++;
                lineStart = index + 1;
            }
            else if (raw[index] == (byte)'\n')
            {
                invalid.Add(inHeaders ? MailSubmissionEmailIssue.RawHeaders : MailSubmissionEmailIssue.BodyTree);
                var line = raw.AsSpan(lineStart, index - lineStart);
                ValidateWireLine(line, inHeaders, ref hasHeader, invalid);
                if (inHeaders && line.Length == 0)
                    inHeaders = false;
                lineStart = index + 1;
            }
        }

        if (lineStart < raw.Length)
        {
            ValidateWireLine(raw.AsSpan(lineStart), inHeaders, ref hasHeader, invalid);
            if (inHeaders)
                invalid.Add(MailSubmissionEmailIssue.RawHeaders);
        }
    }

    private static void ValidateWireLine(
        ReadOnlySpan<byte> line,
        bool inHeaders,
        ref bool hasHeader,
        HashSet<MailSubmissionEmailIssue> invalid)
    {
        if (line.Length > 998)
            invalid.Add(inHeaders ? MailSubmissionEmailIssue.RawHeaders : MailSubmissionEmailIssue.BodyTree);
        if (!inHeaders)
        {
            if (line.Contains((byte)0))
                invalid.Add(MailSubmissionEmailIssue.BodyTree);
            return;
        }
        if (line.Length == 0)
            return;

        var valueStart = 0;
        if (line[0] is (byte)' ' or (byte)'\t')
        {
            if (!hasHeader || IsWhitespaceOnly(line))
                invalid.Add(MailSubmissionEmailIssue.RawHeaders);
        }
        else
        {
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsValidFieldName(line[..colon]))
            {
                invalid.Add(MailSubmissionEmailIssue.RawHeaders);
                return;
            }
            hasHeader = true;
            valueStart = colon + 1;
        }

        var value = line[valueStart..];
        if (HasInvalidHeaderValueByte(value))
        {
            invalid.Add(MailSubmissionEmailIssue.RawHeaders);
            return;
        }
        try
        {
            _ = StrictUtf8.GetCharCount(value);
        }
        catch (DecoderFallbackException)
        {
            invalid.Add(MailSubmissionEmailIssue.RawHeaders);
        }
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<byte> value)
    {
        foreach (ref readonly var character in value)
        {
            if (character is not ((byte)' ' or (byte)'\t'))
                return false;
        }
        return true;
    }

    private static bool IsValidFieldName(ReadOnlySpan<byte> value)
    {
        foreach (ref readonly var character in value)
        {
            if (character is < 33 or > 126)
                return false;
        }
        return true;
    }

    private static bool HasInvalidHeaderValueByte(ReadOnlySpan<byte> value)
    {
        foreach (ref readonly var character in value)
        {
            if (character != (byte)'\t' && (character < 32 || character == 127))
                return true;
        }
        return false;
    }

    private static Header[] Headers(HeaderList headers, string name) =>
        headers.Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void ValidateAddressHeader(
        HeaderList headers,
        string headerName,
        MailSubmissionEmailIssue propertyName,
        bool allowEmpty,
        HashSet<MailSubmissionEmailIssue> invalid)
    {
        var matching = Headers(headers, headerName);
        if (matching.Length == 1
            && !TryParseAddressList(matching[0].Value, allowEmpty, false, out _))
        {
            invalid.Add(propertyName);
        }
    }

    private static void ValidateResentHeaders(HeaderList headers, HashSet<MailSubmissionEmailIssue> invalid)
    {
        var resent = headers
            .Where(header => ResentHeaderProperties.ContainsKey(header.Field))
            .ToArray();
        if (resent.Length == 0)
        {
            if (Headers(headers, "Resent-Reply-To").Length > 0)
                invalid.Add(MailSubmissionEmailIssue.ResentReplyTo);
            return;
        }

        ValidateEveryHeader(
            resent,
            "Resent-Date",
            JmapDate.IsValidRfc5322DateTime,
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-From",
            value => TryParseAddressList(value, false, true, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Sender",
            value => MailboxAddress.TryParse(StrictAddressParserOptions, value, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-To",
            value => TryParseAddressList(value, false, false, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Cc",
            value => TryParseAddressList(value, false, false, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Bcc",
            value => TryParseAddressList(value, true, false, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Message-ID",
            value => JmapEmailCodec.IsValidMessageIdsHeader(value, true),
            invalid);

        ValidateResentCardinality(resent, invalid);
        if (!CanPartitionResentBlocks(resent))
            invalid.Add(MailSubmissionEmailIssue.RawHeaders);
        if (Headers(headers, "Resent-Reply-To").Length > 0)
            invalid.Add(MailSubmissionEmailIssue.ResentReplyTo);
    }

    private static void ValidateEveryHeader(
        IEnumerable<Header> headers,
        string headerName,
        Func<string, bool> isValid,
        HashSet<MailSubmissionEmailIssue> invalid)
    {
        if (headers.Any(header => header.Field.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            && !isValid(header.Value)))
        {
            invalid.Add(ResentHeaderProperties[headerName]);
        }
    }

    private static void ValidateResentCardinality(
        IReadOnlyList<Header> headers,
        HashSet<MailSubmissionEmailIssue> invalid)
    {
        var dateCount = CountHeaders(headers, "Resent-Date");
        var fromHeaders = headers
            .Where(header => header.Field.Equals("Resent-From", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var blockCount = Math.Max(1, Math.Max(dateCount, fromHeaders.Length));
        if (dateCount < blockCount)
            invalid.Add(ResentHeaderProperties["Resent-Date"]);
        if (fromHeaders.Length < blockCount)
            invalid.Add(ResentHeaderProperties["Resent-From"]);

        foreach (var name in ResentHeaderProperties.Keys
            .Where(name => name is not ("Resent-Date" or "Resent-From")))
        {
            if (CountHeaders(headers, name) > blockCount)
                invalid.Add(ResentHeaderProperties[name]);
        }

        var multiFromCount = fromHeaders.Count(ResentFromRequiresSender);
        if (CountHeaders(headers, "Resent-Sender") < multiFromCount)
            invalid.Add(ResentHeaderProperties["Resent-Sender"]);
    }

    private static int CountHeaders(IEnumerable<Header> headers, string name) =>
        headers.Count(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool CanPartitionResentBlocks(Header[] headers)
    {
        var reachable = new bool[headers.Length + 1];
        reachable[0] = true;
        for (var start = 0; start < headers.Length; start++)
        {
            if (!reachable[start])
                continue;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Header? from = null;
            var hasDate = false;
            var hasSender = false;
            for (var end = start; end < headers.Length; end++)
            {
                var header = headers[end];
                if (!names.Add(header.Field))
                    break;
                if (header.Field.Equals("Resent-Date", StringComparison.OrdinalIgnoreCase))
                    hasDate = true;
                else if (header.Field.Equals("Resent-From", StringComparison.OrdinalIgnoreCase))
                    from = header;
                else if (header.Field.Equals("Resent-Sender", StringComparison.OrdinalIgnoreCase))
                    hasSender = true;

                if (hasDate && from is not null && (!ResentFromRequiresSender(from) || hasSender))
                    reachable[end + 1] = true;
            }
        }
        return reachable[^1];
    }

    private static bool ResentFromRequiresSender(Header header) =>
        TryParseAddressList(header.Value, false, true, out var addresses)
        && addresses.Mailboxes.Skip(1).Any();

    private static bool TryParseAddressList(
        string value,
        bool allowEmpty,
        bool mailboxesOnly,
        out InternetAddressList addresses)
    {
        if (allowEmpty && JmapEmailCodec.IsHeaderCfwsOnly(value))
        {
            addresses = new InternetAddressList();
            return true;
        }
        if (!InternetAddressList.TryParse(
                StrictAddressParserOptions,
                value,
                out var parsedAddresses)
            || parsedAddresses is null)
        {
            addresses = new InternetAddressList();
            return false;
        }
        addresses = parsedAddresses;
        return (allowEmpty || addresses.Count > 0)
            && (!mailboxesOnly || addresses.All(address => address is MailboxAddress));
    }

    private static void ValidateMessageIdsHeader(
        HeaderList headers,
        string headerName,
        MailSubmissionEmailIssue propertyName,
        bool requireSingle,
        HashSet<MailSubmissionEmailIssue> invalid)
    {
        var matching = Headers(headers, headerName);
        if (matching.Length == 1
            && !JmapEmailCodec.IsValidMessageIdsHeader(matching[0].Value, requireSingle))
        {
            invalid.Add(propertyName);
        }
    }

    private static bool HasDuplicateMimeHeaders(MimeEntity? entity)
    {
        if (entity is null)
            return false;
        if (entity.Headers
            .Where(header => SingletonMimeHeaders.Contains(header.Field))
            .GroupBy(header => header.Field, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            return true;
        }
        return entity is Multipart multipart
            && multipart.Any(HasDuplicateMimeHeaders);
    }

}
