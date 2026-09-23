using System.Text;
using MimeKit;
using mk8.email.MailWire;

namespace mk8.email.Imap.Presentation.Protocol;

internal enum ImapBinarySectionStatus
{
    Success,
    NotFound,
    NotLeaf,
    UnknownTransferEncoding,
    InvalidContent,
}

internal readonly record struct ImapBinarySection(byte[] Content)
{
    public bool RequiresLiteral8 => Content.AsSpan().Contains((byte)0);
}

internal sealed class ImapMimeMessage : IDisposable
{
    private static readonly FormatOptions WireFormat = CreateWireFormat();
    private readonly MimeMessage _message;

    private ImapMimeMessage(MimeMessage message)
    {
        _message = message;
    }

    public string Body => FormatEntity(_message.Body, extended: false);

    public string BodyStructure => FormatEntity(_message.Body, extended: true);

    public static ImapMimeMessage? TryParse(string rawMessage)
    {
        try
        {
            var bytes = MailWireEncoding.Instance.GetBytes(rawMessage);
            using var stream = new MemoryStream(bytes, writable: false);
            return new ImapMimeMessage(MimeMessage.Load(stream));
        }
        catch (Exception exception) when (
            exception is FormatException or IOException or ParseException)
        {
            return null;
        }
    }

    public bool TryGetSection(string section, out string content)
    {
        content = string.Empty;
        var mimeSuffix = section.EndsWith(".MIME", StringComparison.OrdinalIgnoreCase);
        var path = mimeSuffix ? section[..^5] : section;
        var components = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
            return false;

        MimeEntity? entity = _message.Body;
        for (var index = 0; index < components.Length; index++)
        {
            if (!int.TryParse(components[index], out var partNumber) || partNumber < 1)
                return false;

            entity = SelectPart(entity, partNumber, index == 0);
            if (entity is null)
                return false;
        }

        if (entity is null)
            return false;

        content = mimeSuffix
            ? SerializeHeaders(entity.Headers)
            : SerializeContent(entity);
        return true;
    }

    public ImapBinarySectionStatus GetBinarySection(
        string section,
        out ImapBinarySection binarySection)
    {
        binarySection = new ImapBinarySection([]);
        if (!TrySelectEntity(section, out var entity))
            return ImapBinarySectionStatus.NotFound;
        if (entity is not MimePart part)
            return ImapBinarySectionStatus.NotLeaf;

        var transferEncoding = part.Headers[HeaderId.ContentTransferEncoding];
        if (part.ContentTransferEncoding == ContentEncoding.Default
            && !string.IsNullOrWhiteSpace(transferEncoding)
            && !transferEncoding.Trim().Equals("7bit", StringComparison.OrdinalIgnoreCase))
        {
            return ImapBinarySectionStatus.UnknownTransferEncoding;
        }

        try
        {
            using var decoded = new MemoryStream();
            part.Content?.DecodeTo(decoded);
            var content = decoded.ToArray();
            if (part.ContentType.IsMimeType("text", "*"))
                content = NormalizeTextLineEndings(content);
            binarySection = new ImapBinarySection(content);
            return ImapBinarySectionStatus.Success;
        }
        catch (Exception exception) when (
            exception is FormatException or IOException or ParseException)
        {
            return ImapBinarySectionStatus.InvalidContent;
        }
    }

    public void Dispose()
    {
        _message.Body?.Dispose();
    }

    private static MimeEntity? SelectPart(MimeEntity? entity, int partNumber, bool isRoot)
    {
        if (entity is null)
            return null;

        if (entity is Multipart multipart)
            return partNumber <= multipart.Count ? multipart[partNumber - 1] : null;

        if (entity is MessagePart { Message: not null } messagePart && !isRoot)
            return SelectPart(messagePart.Message.Body, partNumber, isRoot: true);

        return isRoot && partNumber == 1 ? entity : null;
    }

    private bool TrySelectEntity(string section, out MimeEntity? entity)
    {
        entity = _message.Body;
        if (section.Length == 0)
            return entity is not null;

        var components = section.Split('.');
        for (var index = 0; index < components.Length; index++)
        {
            if (!int.TryParse(components[index], out var partNumber) || partNumber < 1)
            {
                entity = null;
                return false;
            }

            entity = SelectPart(entity, partNumber, index == 0);
            if (entity is null)
                return false;
        }
        return true;
    }

    private static byte[] NormalizeTextLineEndings(ReadOnlySpan<byte> content)
    {
        using var normalized = new MemoryStream(content.Length);
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == (byte)'\r')
            {
                normalized.WriteByte((byte)'\r');
                normalized.WriteByte((byte)'\n');
                if (index + 1 < content.Length && content[index + 1] == (byte)'\n')
                    index++;
            }
            else if (content[index] == (byte)'\n')
            {
                normalized.WriteByte((byte)'\r');
                normalized.WriteByte((byte)'\n');
            }
            else
            {
                normalized.WriteByte(content[index]);
            }
        }
        return normalized.ToArray();
    }

    private static string FormatEntity(MimeEntity? entity, bool extended)
    {
        if (entity is Multipart multipart)
        {
            var children = string.Join(' ', multipart.Select(child => FormatEntity(child, extended)));
            var structure = $"({children} \"{QuoteAtom(multipart.ContentType.MediaSubtype)}\"";

            if (extended)
            {
                structure +=
                    $" {FormatParameters(multipart.ContentType.Parameters)}" +
                    $" {FormatDisposition(multipart.ContentDisposition)}" +
                    $" {FormatLanguage(multipart)}" +
                    $" {FormatLocation(multipart)}";
            }

            return structure + ")";
        }

        if (entity is MessagePart { Message: not null } messagePart)
        {
            var metrics = MeasureContent(messagePart);
            var transferEncoding = messagePart.Headers[HeaderId.ContentTransferEncoding];
            var structure =
                $"(\"MESSAGE\" \"RFC822\" {FormatParameters(messagePart.ContentType)} " +
                $"{FormatContentId(messagePart)} " +
                $"{FormatNString(messagePart.Headers[HeaderId.ContentDescription])} " +
                $"\"{FormatTransferEncoding(transferEncoding)}\" " +
                $"{metrics.Octets} {FormatEnvelope(messagePart.Message)} " +
                $"{FormatEntity(messagePart.Message.Body, extended)} {metrics.Lines}";

            if (extended)
                structure += $" {FormatOnePartExtensions(messagePart)}";

            return structure + ")";
        }

        if (entity is MimePart part)
        {
            var metrics = MeasureContent(part);
            var mediaType = QuoteAtom(part.ContentType.MediaType);
            var mediaSubtype = QuoteAtom(part.ContentType.MediaSubtype);
            var structure =
                $"(\"{mediaType}\" \"{mediaSubtype}\" {FormatParameters(part.ContentType)} " +
                $"{FormatContentId(part)} {FormatNString(part.ContentDescription)} " +
                $"\"{FormatEncoding(part.ContentTransferEncoding)}\" " +
                $"{metrics.Octets}";

            if (part.ContentType.IsMimeType("text", "*"))
                structure += $" {metrics.Lines}";

            if (extended)
                structure += $" {FormatOnePartExtensions(part)}";

            return structure + ")";
        }

        return "(\"APPLICATION\" \"OCTET-STREAM\" NIL NIL NIL \"7BIT\" 0)";
    }

    private static string FormatEnvelope(MimeMessage message)
    {
        var date = message.Headers[HeaderId.Date];
        var sender = message.Sender is null
            ? message.From
            : new InternetAddressList { message.Sender };

        return
            $"({FormatNString(date)} {FormatNString(message.Subject)} " +
            $"{FormatAddresses(message.From)} {FormatAddresses(sender)} " +
            $"{FormatAddresses(message.ReplyTo)} {FormatAddresses(message.To)} " +
            $"{FormatAddresses(message.Cc)} {FormatAddresses(message.Bcc)} " +
            $"{FormatNString(message.InReplyTo)} {FormatNString(message.MessageId)})";
    }

    private static string FormatAddresses(InternetAddressList addresses)
    {
        var values = new List<string>();
        AddAddresses(addresses, values);
        return values.Count == 0 ? "NIL" : $"({string.Join(' ', values)})";
    }

    private static void AddAddresses(IEnumerable<InternetAddress> addresses, List<string> values)
    {
        foreach (var address in addresses)
        {
            if (address is GroupAddress group)
            {
                AddAddresses(group.Members, values);
                continue;
            }

            if (address is not MailboxAddress mailbox)
                continue;

            var separator = mailbox.Address.LastIndexOf('@');
            var localPart = separator > 0 ? mailbox.Address[..separator] : mailbox.Address;
            var domain = separator > 0 && separator < mailbox.Address.Length - 1
                ? mailbox.Address[(separator + 1)..]
                : null;
            values.Add(
                $"({FormatNString(mailbox.Name)} NIL {FormatNString(localPart)} {FormatNString(domain)})");
        }
    }

    private static string FormatParameters(ContentType contentType) =>
        FormatParameters(contentType.Parameters);

    private static string FormatParameters(IEnumerable<Parameter> parameters)
    {
        var values = new List<string>();
        foreach (var parameter in parameters)
        {
            values.Add(FormatNString(parameter.Name.ToUpperInvariant()));
            values.Add(FormatNString(parameter.Value));
        }

        return values.Count == 0 ? "NIL" : $"({string.Join(' ', values)})";
    }

    private static string FormatOnePartExtensions(MimeEntity entity) =>
        $"{FormatNString(entity.Headers[HeaderId.ContentMd5]?.Trim())} " +
        $"{FormatDisposition(entity.ContentDisposition)} " +
        $"{FormatLanguage(entity)} " +
        FormatLocation(entity);

    private static string FormatContentId(MimeEntity entity)
    {
        var contentId = entity.Headers[HeaderId.ContentId]?.Trim();
        if (string.IsNullOrEmpty(contentId) && !string.IsNullOrEmpty(entity.ContentId))
            contentId = $"<{entity.ContentId}>";

        return FormatNString(contentId);
    }

    private static string FormatDisposition(ContentDisposition? disposition)
    {
        if (disposition is null)
            return "NIL";

        return
            $"(\"{QuoteAtom(disposition.Disposition)}\" " +
            $"{FormatParameters(disposition.Parameters)})";
    }

    private static string FormatLanguage(MimeEntity entity)
    {
        var header = entity.Headers[HeaderId.ContentLanguage];
        if (string.IsNullOrWhiteSpace(header))
            return "NIL";

        var languages = header
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .Select(FormatNString)
            .ToList();

        return languages.Count switch
        {
            0 => "NIL",
            1 => languages[0],
            _ => $"({string.Join(' ', languages)})",
        };
    }

    private static string FormatLocation(MimeEntity entity)
    {
        var location = entity.Headers[HeaderId.ContentLocation]?.Trim();
        if (string.IsNullOrEmpty(location))
            location = entity.ContentLocation?.OriginalString;

        return FormatNString(location);
    }

    private static string FormatEncoding(ContentEncoding encoding) => encoding switch
    {
        ContentEncoding.Base64 => "BASE64",
        ContentEncoding.Binary => "BINARY",
        ContentEncoding.EightBit => "8BIT",
        ContentEncoding.QuotedPrintable => "QUOTED-PRINTABLE",
        ContentEncoding.UUEncode => "X-UUENCODE",
        _ => "7BIT",
    };

    private static string FormatTransferEncoding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "7BIT";

        return value.Trim().ToUpperInvariant() switch
        {
            "7BIT" => "7BIT",
            "8BIT" => "8BIT",
            "BINARY" => "BINARY",
            "BASE64" => "BASE64",
            "QUOTED-PRINTABLE" => "QUOTED-PRINTABLE",
            var encoding => QuoteAtom(encoding),
        };
    }

    private static string FormatNString(string? value) =>
        string.IsNullOrEmpty(value) ? "NIL" : $"\"{Escape(value)}\"";

    private static string QuoteAtom(string value) => Escape(value).ToUpperInvariant();

    private static string Escape(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string SerializeHeaders(HeaderList headers)
    {
        using var stream = new MemoryStream();
        headers.WriteTo(WireFormat, stream);
        stream.Write("\r\n"u8);
        return MailWireEncoding.Instance.GetString(stream.ToArray());
    }

    private static string SerializeContent(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        entity.WriteTo(WireFormat, stream, contentOnly: true);
        return MailWireEncoding.Instance.GetString(stream.ToArray());
    }

    private static (long Octets, int Lines) MeasureContent(MimeEntity entity)
    {
        using var stream = new CountingWriteStream();
        entity.WriteTo(WireFormat, stream, contentOnly: true);
        return (stream.Length, stream.LineCount);
    }

    private static FormatOptions CreateWireFormat()
    {
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        options.EnsureNewLine = false;
        return options;
    }

    private sealed class CountingWriteStream : Stream
    {
        private long _length;
        private int _newLines;
        private byte _lastByte;

        public int LineCount => _length == 0
            ? 0
            : _newLines + (_lastByte == (byte)'\n' ? 0 : 1);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Count(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer);

        public override void WriteByte(byte value)
        {
            _length++;
            if (value == (byte)'\n')
                _newLines++;
            _lastByte = value;
        }

        private void Count(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
                return;

            _length += buffer.Length;
            foreach (var value in buffer)
            {
                if (value == (byte)'\n')
                    _newLines++;
            }
            _lastByte = buffer[^1];
        }
    }
}
