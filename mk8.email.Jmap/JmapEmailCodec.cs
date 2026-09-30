using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static partial class JmapEmailCodec
{
    public static byte[] GetRawBytes(EmailDB email)
    {
        if (email.RawMessage is not null)
            return email.RawMessage.ToArray();

        var value = email.RawHeaders is null
            ? BuildFallbackMessage(email)
            : email.RawHeaders + "\r\n\r\n" + email.Body;
        return Encoding.Latin1.GetBytes(value);
    }

    public static MimeMessage Parse(EmailDB email)
    {
        using var stream = new MemoryStream(GetRawBytes(email), writable: false);
        return MimeMessage.Load(stream, persistent: false);
    }

    public static MimeMessage Parse(ReadOnlyMemory<byte> raw)
    {
        using var stream = new MemoryStream(raw.ToArray(), writable: false);
        return MimeMessage.Load(stream, persistent: false);
    }

    public static bool TryGetPartContent(
        MimeMessage message,
        string partId,
        out byte[] content,
        out string contentType,
        out string? name)
    {
        content = [];
        contentType = "application/octet-stream";
        name = null;
        var paths = partId.Split('!');
        return paths.Length > 0
            && paths.All(path => path.Length > 0)
            && TryGetPartContent(
                message,
                paths,
                0,
                out content,
                out contentType,
                out name);
    }

    public static bool TryGetPartContentByHash(
        MimeMessage message,
        ReadOnlySpan<byte> pathHash,
        int nestingDepth,
        out string partId,
        out byte[] content,
        out string contentType,
        out string? name)
    {
        partId = string.Empty;
        content = [];
        contentType = "application/octet-stream";
        name = null;
        return pathHash.Length == SHA256.HashSizeInBytes
            && nestingDepth >= 0
            && TryGetPartContentByHash(
                message,
                pathHash.ToArray(),
                nestingDepth,
                null,
                out partId,
                out content,
                out contentType,
                out name);
    }

    private static bool TryGetPartContentByHash(
        MimeMessage message,
        byte[] pathHash,
        int nestingDepth,
        string? prefix,
        out string partId,
        out byte[] content,
        out string contentType,
        out string? name)
    {
        partId = string.Empty;
        content = [];
        contentType = "application/octet-stream";
        name = null;
        var parts = EnumerateLeafEntities(message.Body);
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            var candidate = prefix is null ? part.Path : $"{prefix}!{part.Path}";
            if (nestingDepth == 0 && MatchesPathHash(candidate, pathHash))
            {
                partId = candidate;
                content = GetDecodedContent(part.Entity);
                contentType = part.Entity.ContentType.MimeType.ToProtocolLowerInvariant();
                name = GetPartName(part.Entity);
                return true;
            }
        }
        if (nestingDepth == 0)
            return false;

        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            try
            {
                using var nestedMessage = Parse(GetDecodedContent(part.Entity));
                var nestedPrefix = prefix is null ? part.Path : $"{prefix}!{part.Path}";
                if (TryGetPartContentByHash(
                        nestedMessage,
                        pathHash,
                        nestingDepth - 1,
                        nestedPrefix,
                        out partId,
                        out content,
                        out contentType,
                        out name))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
            }
        }
        return false;
    }

    private static List<(MimeEntity Entity, string Path)> EnumerateLeafEntities(
        MimeEntity? root)
    {
        if (root is null)
            return [];
        var result = new List<(MimeEntity Entity, string Path)>();
        var pending = new Stack<(MimeEntity Entity, string Path, bool IsRoot)>();
        pending.Push((root, "1", true));
        while (pending.TryPop(out var current))
        {
            if (current.Entity is not Multipart multipart)
            {
                result.Add((current.Entity, current.Path));
                continue;
            }
            for (var index = multipart.Count - 1; index >= 0; index--)
            {
                var childPath = current.IsRoot
                    ? (index + 1).ToString(CultureInfo.InvariantCulture)
                    : $"{current.Path}.{index + 1}";
                pending.Push((multipart[index], childPath, false));
            }
        }
        return result;
    }

    private static bool MatchesPathHash(string partId, ReadOnlySpan<byte> expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(partId)),
            expected);

    private static bool TryGetPartContent(
        MimeMessage message,
        IReadOnlyList<string> paths,
        int pathIndex,
        out byte[] content,
        out string contentType,
        out string? name)
    {
        content = [];
        contentType = "application/octet-stream";
        name = null;
        if (!TryGetEntity(message, paths[pathIndex], out var entity))
            return false;
        if (pathIndex < paths.Count - 1)
        {
            try
            {
                using var nestedMessage = Parse(GetDecodedContent(entity));
                return TryGetPartContent(
                    nestedMessage,
                    paths,
                    pathIndex + 1,
                    out content,
                    out contentType,
                    out name);
            }
            catch (FormatException)
            {
                return false;
            }
        }
        if (entity is Multipart)
            return false;
        content = GetDecodedContent(entity);
        contentType = entity.ContentType.MimeType.ToProtocolLowerInvariant();
        name = GetPartName(entity);
        return true;
    }

    private static bool TryGetEntity(
        MimeMessage message,
        string path,
        out MimeEntity entity)
    {
        entity = null!;
        var tokens = path.Split('.');
        if (tokens.Length == 0
            || tokens.Any(token => !int.TryParse(
                token,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index) || index < 1))
        {
            return false;
        }

        MimeEntity? current = message.Body;
        if (current is null)
            return false;
        if (current is Multipart)
        {
            foreach (var token in tokens)
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var oneBased)
                    || current is not Multipart multipart
                    || oneBased > multipart.Count)
                {
                    return false;
                }
                current = multipart[oneBased - 1];
            }
        }
        else if (tokens.Length != 1 || !string.Equals(tokens[0], "1", StringComparison.Ordinal))
        {
            return false;
        }

        entity = current;
        return true;
    }

    internal static MailMessageSnapshot Capture(MimeMessage message, Guid contentSourceId,
        long rawSize, bool includeText, EmailDB? stored = null, string? uploadedContentId = null,
        string? partPrefix = null)
    {
        var metadata = stored is null ? null : new MailStoredMessageSnapshot(stored.Id, stored.FolderId,
            stored.ThreadObjectId ?? stored.Id.ToString("N"), MailMessageFlagMutations.Keywords(stored).ToArray(),
            stored.SizeBytes > 0 ? stored.SizeBytes : rawSize,
            stored.ReceivedAt.Kind == DateTimeKind.Utc ? stored.ReceivedAt : stored.ReceivedAt.ToUniversalTime());
        var body = BuildParts(message.Body);
        var parts = new List<MailMimePartSnapshot>();
        var root = body is null ? (int?)null : CapturePart(body, includeText, parts);
        return new(contentSourceId, uploadedContentId, partPrefix, rawSize, metadata,
            CaptureHeaders(MessageHeaders(message)), root, parts.ToArray());
    }

    private static MailMimeHeaderSnapshot[] CaptureHeaders(IEnumerable<Header> headers) =>
        headers.Select(header => new MailMimeHeaderSnapshot(header.RawField.ToArray(), header.RawValue.ToArray())).ToArray();

    private static int CapturePart(PartDescriptor part, bool includeText, List<MailMimePartSnapshot> parts)
    {
        var (text, encodingProblem) = includeText && part.Type.StartsWith("text/", StringComparison.Ordinal)
            ? DecodeText(part) : ((string?)null, false);
        var children = part.SubParts.Select(child => CapturePart(child, includeText, parts)).ToArray();
        var index = parts.Count;
        parts.Add(new(part.PartId, part.Bytes.LongLength, CaptureHeaders(part.Entity.Headers), part.Name, part.Type,
            part.Charset, part.Disposition, part.ContentId, part.Language, part.Location, text, encodingProblem,
            children));
        return index;
    }

    public static bool IsValidKeyword(string keyword) =>
        keyword.Length is >= 1 and <= 255
        && !string.Equals(keyword, "$recent", StringComparison.OrdinalIgnoreCase)
        && keyword.All(character => character is >= (char)0x21 and <= (char)0x7e
            && character is not ('(' or ')' or '{' or ']'
                or '%' or '*' or '"' or '\\'));

    public static bool HasAttachment(MimeMessage message)
    {
        var selection = SelectBodyParts(BuildParts(message.Body));
        return selection.Attachments.Any(part =>
            !string.Equals(part.Disposition, "inline", StringComparison.OrdinalIgnoreCase));
    }

    internal static string SearchableBodyText(MimeMessage message)
    {
        var text = new List<string>();
        var pending = new Queue<(MimeMessage Message, bool IsAttached)>();
        var visited = new HashSet<MimeMessage>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((message, false));
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.Message))
                continue;
            if (current.IsAttached)
            {
                text.AddRange(
                [
                    current.Message.From.ToString(),
                    current.Message.To.ToString(),
                    current.Message.Cc.ToString(),
                    current.Message.Bcc.ToString(),
                    current.Message.Subject ?? string.Empty,
                ]);
            }

            foreach (var part in Flatten(BuildParts(current.Message.Body)))
            {
                if (part.PartId is not null
                    && part.Type.StartsWith("text/", StringComparison.Ordinal))
                {
                    var value = DecodeText(part).Text;
                    text.Add(string.Equals(part.Type, "text/html", StringComparison.Ordinal) ? JmapHtmlText.Extract(value) : value);
                }
            }
            foreach (var nested in NestedMessages(current.Message.Body))
                pending.Enqueue((nested, true));
        }
        return string.Join('\n', text);
    }

    private static IEnumerable<MimeMessage> NestedMessages(MimeEntity? root)
    {
        if (root is null)
            yield break;
        var pending = new Stack<MimeEntity>();
        pending.Push(root);
        while (pending.TryPop(out var entity))
        {
            if (entity is MessagePart { Message: not null } messagePart)
            {
                yield return messagePart.Message;
                continue;
            }
            if (entity is not Multipart multipart)
                continue;
            for (var index = multipart.Count - 1; index >= 0; index--)
                pending.Push(multipart[index]);
        }
    }

    private static PartDescriptor? BuildParts(MimeEntity? entity) =>
        entity is null
            ? null
            : BuildPart(entity, "1", isRoot: true);

    private static PartDescriptor BuildPart(
        MimeEntity entity,
        string path,
        bool isRoot)
    {
        var subParts = new List<PartDescriptor>();
        if (entity is Multipart multipart)
        {
            for (var index = 0; index < multipart.Count; index++)
            {
                var childPath = isRoot
                    ? (index + 1).ToString(CultureInfo.InvariantCulture)
                    : $"{path}.{index + 1}";
                subParts.Add(BuildPart(
                    multipart[index],
                    childPath,
                    isRoot: false));
            }
        }

        var partId = entity is Multipart ? null : path;
        var bytes = GetDecodedContent(entity);
        var type = entity.ContentType.MimeType.ToProtocolLowerInvariant();
        var charset = type.StartsWith("text/", StringComparison.Ordinal)
            ? entity.ContentType.Charset ?? "us-ascii"
            : null;
        var languageHeaders = entity.Headers.Where(header =>
            header.Field.Equals("Content-Language", StringComparison.OrdinalIgnoreCase));
        List<string>? languages = null;
        foreach (var languageHeader in languageHeaders)
        {
            if (!JmapLanguageTag.TryParseHeader(languageHeader.Value, out var parsedLanguages))
            {
                languages = null;
                break;
            }
            languages ??= [];
            languages.AddRange(parsedLanguages);
        }
        var fileName = GetPartName(entity);
        return new PartDescriptor(
            entity,
            partId,
            bytes,
            fileName,
            type,
            charset,
            entity.ContentDisposition?.Disposition?.ToProtocolLowerInvariant(),
            entity.ContentId,
            languages is { Count: > 0 } ? languages.ToArray() : null,
            entity.ContentLocation?.ToString(),
            subParts);
    }

    private static string? GetPartName(MimeEntity entity) =>
        entity.ContentDisposition?.FileName ?? entity.ContentType.Name;

    private static byte[] GetDecodedContent(MimeEntity entity)
    {
        if (entity is Multipart)
            return [];
        if (entity is MessagePart { Message: not null } messagePart)
        {
            using var messageStream = new MemoryStream();
            messagePart.Message.WriteTo(messageStream);
            return messageStream.ToArray();
        }
        if (entity is not MimePart part || part.Content is null)
            return [];
        using var output = new MemoryStream();
        part.Content.DecodeTo(output);
        return output.ToArray();
    }

    private static IEnumerable<PartDescriptor> Flatten(PartDescriptor? root)
    {
        if (root is null)
            yield break;
        yield return root;
        foreach (var child in root.SubParts)
        {
            foreach (var descendant in Flatten(child))
                yield return descendant;
        }
    }

    private static bool IsInlineBodyPart(PartDescriptor part) =>
        !string.Equals(part.Disposition, "attachment", StringComparison.OrdinalIgnoreCase)
        && (part.Name is null || IsInlineMedia(part.Type));

    private static bool IsInlineMedia(string type) =>
        type.StartsWith("image/", StringComparison.Ordinal)
        || type.StartsWith("audio/", StringComparison.Ordinal)
        || type.StartsWith("video/", StringComparison.Ordinal);

    private static bool IsAttachment(
        PartDescriptor part,
        HashSet<string> bodyIds,
        HashSet<string> commonBodyIds) =>
        !bodyIds.Contains(part.PartId!)
        || IsInlineMedia(part.Type) && !commonBodyIds.Contains(part.PartId!);

    private static BodyPartSelection SelectBodyParts(PartDescriptor? root)
    {
        var leafParts = Flatten(root).Where(part => part.PartId is not null).ToArray();
        var textCandidates = leafParts
            .Where(part => IsInlineBodyPart(part) && string.Equals(part.Type, "text/plain", StringComparison.Ordinal))
            .ToArray();
        var htmlCandidates = leafParts
            .Where(part => IsInlineBodyPart(part) && string.Equals(part.Type, "text/html", StringComparison.Ordinal))
            .ToArray();
        var textBodyValues = new List<PartDescriptor>();
        var htmlBodyValues = new List<PartDescriptor>();
        if (root is not null)
        {
            SelectDisplayedParts(
                [root],
                "mixed",
                inAlternative: false,
                htmlBodyValues,
                textBodyValues);
        }
        var textBody = textBodyValues
            .DistinctBy(part => part.PartId, StringComparer.Ordinal)
            .ToArray();
        var htmlBody = htmlBodyValues
            .DistinctBy(part => part.PartId, StringComparer.Ordinal)
            .ToArray();
        var bodyIds = textBody.Concat(htmlBody)
            .Select(part => part.PartId!)
            .ToHashSet(StringComparer.Ordinal);
        var commonBodyIds = textBody.Select(part => part.PartId!)
            .Intersect(htmlBody.Select(part => part.PartId!), StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var attachments = leafParts
            .Where(part => IsAttachment(part, bodyIds, commonBodyIds))
            .ToArray();
        return new BodyPartSelection(
            leafParts,
            textCandidates,
            htmlCandidates,
            textBody,
            htmlBody,
            attachments);
    }

    private static void SelectDisplayedParts(
        IReadOnlyList<PartDescriptor> parts,
        string multipartType,
        bool inAlternative,
        List<PartDescriptor>? htmlBody,
        List<PartDescriptor>? textBody)
    {
        var textLength = textBody?.Count ?? -1;
        var htmlLength = htmlBody?.Count ?? -1;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Type.StartsWith("multipart/", StringComparison.Ordinal))
            {
                var subtype = part.Type["multipart/".Length..];
                SelectDisplayedParts(
                    part.SubParts,
                    subtype,
                    inAlternative || string.Equals(subtype, "alternative", StringComparison.Ordinal),
                    htmlBody,
                    textBody);
                continue;
            }

            var inlineMedia = IsInlineMedia(part.Type);
            var isInline = !string.Equals(
                    part.Disposition,
                    "attachment",
                    StringComparison.OrdinalIgnoreCase)
                && (part.Type is "text/plain" or "text/html" || inlineMedia)
                && (index == 0
                    || !string.Equals(multipartType, "related", StringComparison.Ordinal)
                        && (inlineMedia || part.Name is null));
            if (!isInline)
                continue;

            if (string.Equals(multipartType, "alternative", StringComparison.Ordinal))
            {
                if (string.Equals(part.Type, "text/plain", StringComparison.Ordinal))
                    textBody?.Add(part);
                else if (string.Equals(part.Type, "text/html", StringComparison.Ordinal))
                    htmlBody?.Add(part);
                continue;
            }

            if (inAlternative)
            {
                if (string.Equals(part.Type, "text/plain", StringComparison.Ordinal))
                    htmlBody = null;
                else if (string.Equals(part.Type, "text/html", StringComparison.Ordinal))
                    textBody = null;
            }
            textBody?.Add(part);
            htmlBody?.Add(part);
        }

        if (!string.Equals(multipartType, "alternative", StringComparison.Ordinal) || textBody is null || htmlBody is null)
            return;
        if (textBody.Count == textLength && htmlBody.Count != htmlLength)
            textBody.AddRange(htmlBody.Skip(htmlLength));
        if (htmlBody.Count == htmlLength && textBody.Count != textLength)
            htmlBody.AddRange(textBody.Skip(textLength));
    }

    private static (string Text, bool EncodingProblem) DecodeText(PartDescriptor part)
    {
        var transferEncodingProblem = HasUnknownTransferEncoding(part.Entity);
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(
                part.Charset ?? "us-ascii",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return (Encoding.UTF8.GetString(part.Bytes), true);
        }

        try
        {
            return (encoding.GetString(part.Bytes), transferEncodingProblem);
        }
        catch (DecoderFallbackException)
        {
            var replacementEncoding = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ReplacementFallback,
                new DecoderReplacementFallback("\ufffd"));
            return (replacementEncoding.GetString(part.Bytes), true);
        }
    }

    private static bool HasUnknownTransferEncoding(MimeEntity entity)
    {
        var value = entity.Headers[HeaderId.ContentTransferEncoding];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim().ToProtocolLowerInvariant() is not (
            "7bit" or "8bit" or "binary" or "base64" or "quoted-printable"
            or "uuencode" or "x-uuencode" or "uue" or "x-uue");
    }

    internal static IReadOnlyList<Header> MessageHeaders(MimeMessage message) =>
        message.Headers
            .Concat(message.Body?.Headers ?? [])
            .OrderBy(header => header.Offset < 0 ? long.MaxValue : header.Offset)
            .ToArray();

    internal static string SearchableHeaderText(MimeMessage message, string name) =>
        string.Join(
            '\n',
            MessageHeaders(message)
                .Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(NormalizeDecodedHeaderText));

    internal static string FirstAddressInLastHeader(MimeMessage message, string name)
    {
        var header = LastHeader(message, name);
        if (header is null)
            return string.Empty;

        var (value, rawTabMarker) = ProtectRawHeaderTabs(header);
        value = NormalizeDecodedHeaderText(value);
        if (!InternetAddressList.TryParse(value, out var addresses))
            return string.Empty;

        var mailbox = addresses.Mailboxes.FirstOrDefault();
        if (mailbox is null)
            return string.Empty;
        var displayName = NormalizeAddressText(mailbox.Name, rawTabMarker);
        return string.IsNullOrEmpty(displayName) ? mailbox.Address : displayName;
    }

    internal static string LastTextHeader(MimeMessage message, string name)
    {
        var header = LastHeader(message, name);
        return header is null ? string.Empty : NormalizeDecodedHeaderText(header);
    }

    internal static DateTimeOffset? LastDateHeader(MimeMessage message, string name)
    {
        var header = LastHeader(message, name);
        return header is not null && DateUtils.TryParse(header.Value, out var value)
            ? value
            : null;
    }

    private static Header? LastHeader(MimeMessage message, string name) =>
        MessageHeaders(message)
            .LastOrDefault(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeDecodedHeaderText(Header header)
    {
        var (value, rawTabMarker) = ProtectRawHeaderTabs(header);
        var normalized = NormalizeDecodedHeaderText(value);
        return rawTabMarker is null
            ? normalized
            : normalized.Replace(rawTabMarker, "\t", StringComparison.Ordinal);
    }

    private static (string Value, string? RawTabMarker) ProtectRawHeaderTabs(Header header)
    {
        var value = header.Value;
        var raw = header.RawValue;
        var rawTabCount = raw.Count(character => character == (byte)'\t');
        if (rawTabCount == 0 || !value.Contains('\t', StringComparison.Ordinal))
            return (value, null);

        var marker = "\ue000\ue001";
        while (value.Contains(marker, StringComparison.Ordinal))
            marker += '\ue001';
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        var protectedRaw = new byte[checked(
            raw.Length + rawTabCount * (markerBytes.Length - 1))];
        var destination = 0;
        foreach (var character in raw)
        {
            if (character == (byte)'\t')
            {
                markerBytes.CopyTo(protectedRaw, destination);
                destination += markerBytes.Length;
            }
            else
            {
                protectedRaw[destination++] = character;
            }
        }

        var protectedHeader = header.Clone();
        protectedHeader.SetRawValue(protectedRaw);
        var protectedValue = protectedHeader.Value;
        var candidate = new StringBuilder(protectedValue.Length);
        var originalIndex = 0;
        for (var protectedIndex = 0; protectedIndex < protectedValue.Length;)
        {
            if (protectedValue.AsSpan(protectedIndex).StartsWith(
                    marker,
                    StringComparison.Ordinal))
            {
                if (originalIndex < value.Length && value[originalIndex] == '\t')
                {
                    candidate.Append(marker);
                    originalIndex++;
                }
                protectedIndex += marker.Length;
                continue;
            }

            var character = protectedValue[protectedIndex++];
            if (originalIndex >= value.Length || value[originalIndex] != character)
                return (value, null);
            candidate.Append(character);
            originalIndex++;
        }

        return originalIndex == value.Length
            ? (candidate.ToString(), marker)
            : (value, null);
    }

    private static string NormalizeAddressText(string? value, string? rawTabMarker)
    {
        var normalized = NormalizeDecodedHeaderText(value);
        return rawTabMarker is null
            ? normalized
            : normalized.Replace(rawTabMarker, "\t", StringComparison.Ordinal);
    }

    private static string NormalizeDecodedHeaderText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormC);
        if (!normalized.Any(char.IsControl))
            return normalized;

        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsControl(character))
                result.Append(character);
        }
        return result.ToString();
    }

    private static List<string>? ParseMessageIds(string value)
    {
        var result = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            if (!TrySkipHeaderCfws(value, ref index))
                return null;
            if (index == value.Length)
                break;
            if (value[index] != '<'
                || !TryParseBracketedMessageId(value, index, out var parsed, out index))
            {
                return null;
            }
            result.Add(parsed);
        }
        return result.Count == 0 ? null : result;
    }

    internal static bool IsValidMessageIdsHeader(string value, bool requireSingle)
    {
        var parsed = ParseMessageIds(value);
        return parsed is not null && (!requireSingle || parsed.Count == 1);
    }

    private static bool TryParseBracketedMessageId(
        string value,
        int start,
        out string parsed,
        out int end)
    {
        parsed = string.Empty;
        end = start;
        var close = start;
        while ((close = value.IndexOf('>', close + 1)) >= 0)
        {
            try
            {
                var candidate = MimeUtils.ParseMessageId(value[start..(close + 1)]);
                if (candidate is not null
                    && JmapMessageId.TryParseParsedForm(candidate, out parsed))
                {
                    end = close + 1;
                    return true;
                }
            }
            catch (ParseException)
            {
            }
        }
        return false;
    }

    private static bool TrySkipHeaderCfws(string value, ref int index)
    {
        while (index < value.Length)
        {
            if (value[index] is ' ' or '\t' or '\r' or '\n')
            {
                index++;
                continue;
            }
            if (value[index] != '(')
                return true;

            var depth = 1;
            index++;
            while (index < value.Length && depth > 0)
            {
                switch (value[index++])
                {
                    case '\\' when index < value.Length:
                        index++;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                }
            }
            if (depth > 0)
                return false;
        }
        return true;
    }

    internal static bool IsHeaderCfwsOnly(string value)
    {
        var index = 0;
        return TrySkipHeaderCfws(value, ref index) && index == value.Length;
    }

    private static string BuildFallbackMessage(EmailDB email)
    {
        var builder = new StringBuilder()
            .Append("From: ").Append(email.Sender).Append("\r\n")
            .Append("To: ").Append(email.Recipient).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(email.Cc))
            builder.Append("Cc: ").Append(email.Cc).Append("\r\n");
        builder.Append("Subject: ").Append(email.Subject).Append("\r\n")
            .Append("Date: ").Append(email.ReceivedAt.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(email.MessageId))
            builder.Append("Message-ID: ").Append(email.MessageId).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(email.InReplyTo))
            builder.Append("In-Reply-To: ").Append(email.InReplyTo).Append("\r\n");
        return builder
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("\r\n")
            .Append(email.Body)
            .ToString();
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WhiteSpaceRegex();

    private sealed record PartDescriptor(
        MimeEntity Entity,
        string? PartId,
        byte[] Bytes,
        string? Name,
        string Type,
        string? Charset,
        string? Disposition,
        string? ContentId,
        string[]? Language,
        string? Location,
        IReadOnlyList<PartDescriptor> SubParts);

    private sealed record BodyPartSelection(
        IReadOnlyList<PartDescriptor> LeafParts,
        IReadOnlyList<PartDescriptor> TextCandidates,
        IReadOnlyList<PartDescriptor> HtmlCandidates,
        IReadOnlyList<PartDescriptor> TextBody,
        IReadOnlyList<PartDescriptor> HtmlBody,
        IReadOnlyList<PartDescriptor> Attachments);
}
