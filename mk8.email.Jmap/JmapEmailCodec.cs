using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapEmailProjectionOptions(
    IReadOnlyList<string> Properties,
    IReadOnlyList<string> BodyProperties,
    bool FetchTextBodyValues,
    bool FetchHtmlBodyValues,
    bool FetchAllBodyValues,
    int MaxBodyValueBytes);

internal static partial class JmapEmailCodec
{
    public static readonly IReadOnlyList<string> DefaultProperties =
    [
        "id", "blobId", "threadId", "mailboxIds", "keywords", "size",
        "receivedAt", "messageId", "inReplyTo", "references", "sender",
        "from", "to", "cc", "bcc", "replyTo", "subject", "sentAt",
        "hasAttachment", "preview", "bodyValues", "textBody", "htmlBody",
        "attachments",
    ];

    public static readonly IReadOnlyList<string> ParseDefaultProperties =
    [
        "messageId", "inReplyTo", "references", "sender", "from", "to",
        "cc", "bcc", "replyTo", "subject", "sentAt", "hasAttachment",
        "preview", "bodyValues", "textBody", "htmlBody", "attachments",
    ];

    public static readonly IReadOnlyList<string> DefaultBodyProperties =
    [
        "partId", "blobId", "size", "name", "type", "charset",
        "disposition", "cid", "language", "location",
    ];

    private static readonly IReadOnlySet<string> FixedProperties = new HashSet<string>(
        DefaultProperties.Concat(["headers", "bodyStructure"]),
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> FixedBodyProperties = new HashSet<string>(
        DefaultBodyProperties.Concat(["headers", "subParts"]),
        StringComparer.Ordinal);

    public static bool IsValidProperty(string property) =>
        FixedProperties.Contains(property) || TryParseHeaderProperty(property, out _);

    public static bool IsValidBodyProperty(string property) =>
        FixedBodyProperties.Contains(property) || TryParseHeaderProperty(property, out _);

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
        contentType = entity.ContentType.MimeType.ToLowerInvariant();
        name = entity is MimePart part ? part.FileName : null;
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
        else if (tokens.Length != 1 || tokens[0] != "1")
        {
            return false;
        }

        entity = current;
        return true;
    }

    public static JsonObject BuildEmail(
        MimeMessage message,
        JmapEmailProjectionOptions options,
        Guid blobSourceId,
        EmailDB? storedEmail = null,
        string? uploadedBlobId = null,
        long? rawSize = null,
        string? blobPartPrefix = null)
    {
        var requested = options.Properties.ToHashSet(StringComparer.Ordinal);
        var parts = BuildParts(message.Body, blobSourceId, blobPartPrefix);
        var selection = SelectBodyParts(parts);
        var leafParts = selection.LeafParts;
        var textCandidates = selection.TextCandidates;
        var htmlCandidates = selection.HtmlCandidates;
        var textBody = selection.TextBody;
        var htmlBody = selection.HtmlBody;
        var attachments = selection.Attachments;
        var result = new JsonObject();
        foreach (var property in options.Properties)
        {
            switch (property)
            {
                case "id":
                    result[property] = storedEmail is null ? null : JmapId.Email(storedEmail.Id);
                    break;
                case "blobId":
                    result[property] = storedEmail is null
                        ? uploadedBlobId
                        : JmapId.RawBlob(storedEmail.Id);
                    break;
                case "threadId":
                    result[property] = storedEmail is null
                        ? null
                        : JmapId.Thread(storedEmail.ThreadObjectId ?? storedEmail.Id.ToString("N"));
                    break;
                case "mailboxIds":
                    result[property] = storedEmail is null
                        ? null
                        : new JsonObject { [JmapId.Mailbox(storedEmail.FolderId)] = true };
                    break;
                case "keywords":
                    result[property] = storedEmail is null ? null : BuildKeywords(storedEmail);
                    break;
                case "size":
                    result[property] = storedEmail is null
                        ? rawSize ?? message.Headers.Count + GetEntitySize(message.Body)
                        : storedEmail.SizeBytes > 0
                            ? storedEmail.SizeBytes
                            : GetRawBytes(storedEmail).Length;
                    break;
                case "receivedAt":
                    result[property] = storedEmail is null
                        ? null
                        : FormatUtcDate(storedEmail.ReceivedAt);
                    break;
                case "headers":
                    result[property] = BuildHeaders(message.Headers);
                    break;
                case "messageId":
                    result[property] = HeaderValue(message.Headers, "Message-ID", HeaderForm.MessageIds, false);
                    break;
                case "inReplyTo":
                    result[property] = HeaderValue(message.Headers, "In-Reply-To", HeaderForm.MessageIds, false);
                    break;
                case "references":
                    result[property] = HeaderValue(message.Headers, "References", HeaderForm.MessageIds, false);
                    break;
                case "sender":
                    result[property] = HeaderValue(message.Headers, "Sender", HeaderForm.Addresses, false);
                    break;
                case "from":
                    result[property] = HeaderValue(message.Headers, "From", HeaderForm.Addresses, false);
                    break;
                case "to":
                    result[property] = HeaderValue(message.Headers, "To", HeaderForm.Addresses, false);
                    break;
                case "cc":
                    result[property] = HeaderValue(message.Headers, "Cc", HeaderForm.Addresses, false);
                    break;
                case "bcc":
                    result[property] = HeaderValue(message.Headers, "Bcc", HeaderForm.Addresses, false);
                    break;
                case "replyTo":
                    result[property] = HeaderValue(message.Headers, "Reply-To", HeaderForm.Addresses, false);
                    break;
                case "subject":
                    result[property] = HeaderValue(message.Headers, "Subject", HeaderForm.Text, false);
                    break;
                case "sentAt":
                    result[property] = HeaderValue(message.Headers, "Date", HeaderForm.Date, false);
                    break;
                case "bodyStructure":
                    result[property] = parts is null
                        ? null
                        : BuildPartJson(parts, options.BodyProperties, includeSubParts: true);
                    break;
                case "bodyValues":
                    result[property] = BuildBodyValues(
                        leafParts,
                        textBody,
                        htmlBody,
                        options);
                    break;
                case "textBody":
                    result[property] = BuildPartList(textBody, options.BodyProperties);
                    break;
                case "htmlBody":
                    result[property] = BuildPartList(htmlBody, options.BodyProperties);
                    break;
                case "attachments":
                    result[property] = BuildPartList(attachments, options.BodyProperties);
                    break;
                case "hasAttachment":
                    result[property] = attachments.Any(part =>
                        !string.Equals(part.Disposition, "inline", StringComparison.OrdinalIgnoreCase));
                    break;
                case "preview":
                    result[property] = BuildPreview(textCandidates, htmlCandidates);
                    break;
                default:
                    if (TryParseHeaderProperty(property, out var headerProperty))
                    {
                        result[property] = HeaderValue(
                            message.Headers,
                            headerProperty.Name,
                            headerProperty.Form,
                            headerProperty.All);
                    }
                    break;
            }
        }

        if (!requested.Contains("id") && storedEmail is not null)
            result["id"] = JmapId.Email(storedEmail.Id);
        return result;
    }

    public static bool TryValidateProperties(
        IReadOnlyList<string> properties,
        bool bodyProperties,
        out string? invalidProperty)
    {
        invalidProperty = properties.FirstOrDefault(property =>
            bodyProperties ? !IsValidBodyProperty(property) : !IsValidProperty(property));
        return invalidProperty is null;
    }

    public static JsonObject BuildKeywords(EmailDB email)
    {
        var result = new JsonObject();
        if (email.IsRead) result["$seen"] = true;
        if (email.IsFlagged) result["$flagged"] = true;
        if (email.IsDraft) result["$draft"] = true;
        if (email.IsAnswered) result["$answered"] = true;
        foreach (var keyword in email.Keywords ?? [])
        {
            var normalized = keyword.ToLowerInvariant();
            if (IsValidKeyword(normalized) && !result.ContainsKey(normalized))
                result[normalized] = true;
        }
        return result;
    }

    public static bool IsValidKeyword(string keyword) =>
        keyword.Length is >= 1 and <= 255
        && keyword.All(character => character is >= (char)0x21 and <= (char)0x7e
            && character is not ('(' or ')' or '{' or ']' or '%' or '*' or '"' or '\\'));

    public static string FormatUtcDate(DateTime value) =>
        JmapDate.FormatUtc(value);

    public static string FormatDate(DateTimeOffset value) =>
        JmapDate.FormatDate(value);

    public static bool HasAttachment(MimeMessage message)
    {
        var selection = SelectBodyParts(BuildParts(message.Body, Guid.Empty, blobPartPrefix: null));
        return selection.Attachments.Any(part =>
            !string.Equals(part.Disposition, "inline", StringComparison.OrdinalIgnoreCase));
    }

    private static PartDescriptor? BuildParts(
        MimeEntity? entity,
        Guid blobSourceId,
        string? blobPartPrefix) =>
        entity is null
            ? null
            : BuildPart(entity, "1", blobSourceId, blobPartPrefix, isRoot: true);

    private static PartDescriptor BuildPart(
        MimeEntity entity,
        string path,
        Guid blobSourceId,
        string? blobPartPrefix,
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
                    blobSourceId,
                    blobPartPrefix,
                    isRoot: false));
            }
        }

        var partId = entity is Multipart ? null : path;
        var bytes = GetDecodedContent(entity);
        var type = entity.ContentType.MimeType.ToLowerInvariant();
        var charset = type.StartsWith("text/", StringComparison.Ordinal)
            ? entity.ContentType.Charset ?? "us-ascii"
            : null;
        var languageHeader = entity.Headers.FirstOrDefault(header =>
            header.Field.Equals("Content-Language", StringComparison.OrdinalIgnoreCase));
        var languages = languageHeader?.Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var fileName = entity is MimePart mimePart ? mimePart.FileName : null;
        return new PartDescriptor(
            entity,
            partId,
            partId is null
                ? null
                : JmapId.BodyPartBlob(
                    blobSourceId,
                    blobPartPrefix is null ? partId : $"{blobPartPrefix}!{partId}"),
            bytes,
            fileName,
            type,
            charset,
            entity.ContentDisposition?.Disposition?.ToLowerInvariant(),
            entity.ContentId,
            languages is { Length: > 0 } ? languages : null,
            entity.ContentLocation?.ToString(),
            subParts);
    }

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

    private static int GetEntitySize(MimeEntity? entity)
    {
        if (entity is null)
            return 0;
        using var stream = new MemoryStream();
        entity.WriteTo(stream);
        return checked((int)Math.Min(stream.Length, int.MaxValue));
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
        IReadOnlySet<string> bodyIds,
        IReadOnlySet<string> commonBodyIds) =>
        !bodyIds.Contains(part.PartId!)
        || IsInlineMedia(part.Type) && !commonBodyIds.Contains(part.PartId!);

    private static BodyPartSelection SelectBodyParts(PartDescriptor? root)
    {
        var leafParts = Flatten(root).Where(part => part.PartId is not null).ToArray();
        var textCandidates = leafParts
            .Where(part => IsInlineBodyPart(part) && part.Type == "text/plain")
            .ToArray();
        var htmlCandidates = leafParts
            .Where(part => IsInlineBodyPart(part) && part.Type == "text/html")
            .ToArray();
        var inlineMedia = leafParts
            .Where(part => IsInlineBodyPart(part) && IsInlineMedia(part.Type))
            .ToArray();
        var textBody = (textCandidates.Length > 0 ? textCandidates : htmlCandidates)
            .Concat(inlineMedia)
            .DistinctBy(part => part.PartId, StringComparer.Ordinal)
            .ToArray();
        var htmlBody = (htmlCandidates.Length > 0 ? htmlCandidates : textCandidates)
            .Concat(inlineMedia)
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

    private static JsonArray BuildPartList(
        IEnumerable<PartDescriptor> parts,
        IReadOnlyList<string> properties,
        bool includeSubParts = false)
    {
        var result = new JsonArray();
        foreach (var part in parts)
            result.Add(BuildPartJson(part, properties, includeSubParts));
        return result;
    }

    private static JsonObject BuildPartJson(
        PartDescriptor part,
        IReadOnlyList<string> properties,
        bool includeSubParts)
    {
        var result = new JsonObject();
        foreach (var property in properties)
        {
            switch (property)
            {
                case "partId": result[property] = part.PartId; break;
                case "blobId": result[property] = part.BlobId; break;
                case "size": result[property] = part.Bytes.Length; break;
                case "headers": result[property] = BuildHeaders(part.Entity.Headers); break;
                case "name": result[property] = part.Name; break;
                case "type": result[property] = part.Type; break;
                case "charset": result[property] = part.Charset; break;
                case "disposition": result[property] = part.Disposition; break;
                case "cid": result[property] = part.ContentId; break;
                case "language":
                    result[property] = part.Language is null
                        ? null
                        : JmapMethodHelpers.ToJsonArray(part.Language);
                    break;
                case "location": result[property] = part.Location; break;
                case "subParts":
                    result[property] = part.SubParts.Count == 0
                        ? null
                        : includeSubParts
                            ? BuildPartList(part.SubParts, properties, includeSubParts: true)
                            : null;
                    break;
                default:
                    if (TryParseHeaderProperty(property, out var headerProperty))
                    {
                        result[property] = HeaderValue(
                            part.Entity.Headers,
                            headerProperty.Name,
                            headerProperty.Form,
                            headerProperty.All);
                    }
                    break;
            }
        }
        if (includeSubParts && part.SubParts.Count > 0 && !result.ContainsKey("subParts"))
            result["subParts"] = BuildPartList(part.SubParts, properties, includeSubParts: true);
        return result;
    }

    private static JsonObject BuildBodyValues(
        IReadOnlyList<PartDescriptor> allParts,
        IReadOnlyList<PartDescriptor> textBody,
        IReadOnlyList<PartDescriptor> htmlBody,
        JmapEmailProjectionOptions options)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (options.FetchAllBodyValues)
        {
            foreach (var part in allParts.Where(part => part.Type.StartsWith("text/", StringComparison.Ordinal)))
                selected.Add(part.PartId!);
        }
        if (options.FetchTextBodyValues)
        {
            foreach (var part in textBody.Where(part => part.Type.StartsWith("text/", StringComparison.Ordinal)))
                selected.Add(part.PartId!);
        }
        if (options.FetchHtmlBodyValues)
        {
            foreach (var part in htmlBody.Where(part => part.Type.StartsWith("text/", StringComparison.Ordinal)))
                selected.Add(part.PartId!);
        }

        var result = new JsonObject();
        foreach (var part in allParts.Where(part => part.PartId is not null && selected.Contains(part.PartId)))
        {
            var (text, encodingProblem) = DecodeText(part);
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var truncated = TruncateUtf8(text, options.MaxBodyValueBytes);
            result[part.PartId!] = new JsonObject
            {
                ["value"] = truncated.Value,
                ["isEncodingProblem"] = encodingProblem,
                ["isTruncated"] = truncated.IsTruncated,
            };
        }
        return result;
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

        return value.Trim().ToLowerInvariant() is not (
            "7bit" or "8bit" or "binary" or "base64" or "quoted-printable"
            or "uuencode" or "x-uuencode" or "uue" or "x-uue");
    }

    private static (string Value, bool IsTruncated) TruncateUtf8(string value, int maximumBytes)
    {
        if (maximumBytes <= 0 || Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return (value, false);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maximumBytes)
                break;
            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return (builder.ToString(), true);
    }

    private static string BuildPreview(
        IReadOnlyList<PartDescriptor> textParts,
        IReadOnlyList<PartDescriptor> htmlParts)
    {
        var source = textParts.FirstOrDefault() ?? htmlParts.FirstOrDefault();
        if (source is null)
            return string.Empty;
        var value = DecodeText(source).Text;
        if (source.Type == "text/html")
            value = HtmlTagRegex().Replace(value, " ");
        value = WhiteSpaceRegex().Replace(value, " ").Trim();
        return TruncateRunes(value, 256);
    }

    private static string TruncateRunes(string value, int maximumRunes)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == maximumRunes)
                break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static JsonArray BuildHeaders(HeaderList headers)
    {
        var result = new JsonArray();
        foreach (var header in headers)
        {
            result.Add(new JsonObject
            {
                ["name"] = header.Field,
                ["value"] = RawHeaderValue(header),
            });
        }
        return result;
    }

    private static JsonNode? HeaderValue(
        HeaderList headers,
        string name,
        HeaderForm form,
        bool all)
    {
        var matching = headers
            .Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (all)
        {
            var result = new JsonArray();
            foreach (var header in matching)
                result.Add(ParseHeaderValue(header, form));
            return result;
        }
        return matching.Length == 0 ? null : ParseHeaderValue(matching[^1], form);
    }

    private static JsonNode? ParseHeaderValue(Header header, HeaderForm form)
    {
        return form switch
        {
            HeaderForm.Raw => JsonValue.Create(RawHeaderValue(header)),
            HeaderForm.Text => JsonValue.Create(NormalizeDecodedHeaderText(header.Value)),
            HeaderForm.Addresses => ParseAddresses(header.Value, grouped: false),
            HeaderForm.GroupedAddresses => ParseAddresses(header.Value, grouped: true),
            HeaderForm.MessageIds => ParseMessageIds(header.Value),
            HeaderForm.Date => DateTimeOffset.TryParse(
                    header.Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var date)
                ? JsonValue.Create(FormatDate(date))
                : null,
            HeaderForm.URLs => ParseUrls(header.Value),
            _ => null,
        };
    }

    private static string RawHeaderValue(Header header)
    {
        var value = Encoding.UTF8.GetString(header.RawValue)
            .TrimEnd('\r', '\n')
            .Replace("\0", string.Empty, StringComparison.Ordinal);
        return value;
    }

    private static JsonNode? ParseAddresses(string value, bool grouped)
    {
        if (!InternetAddressList.TryParse(value, out var addresses))
            return null;
        if (!grouped)
        {
            var flattened = new JsonArray();
            foreach (var mailbox in addresses.Mailboxes)
                flattened.Add(BuildAddress(mailbox));
            return flattened;
        }

        var groups = new JsonArray();
        var ungrouped = new List<MailboxAddress>();
        foreach (var address in addresses)
        {
            if (address is MailboxAddress mailbox)
            {
                ungrouped.Add(mailbox);
                continue;
            }
            FlushUngrouped();
            if (address is GroupAddress group)
            {
                var groupName = NormalizeDecodedHeaderText(group.Name);
                groups.Add(new JsonObject
                {
                    ["name"] = string.IsNullOrEmpty(groupName) ? null : groupName,
                    ["addresses"] = BuildAddressArray(group.Members.Mailboxes),
                });
            }
        }
        FlushUngrouped();
        return groups;

        void FlushUngrouped()
        {
            if (ungrouped.Count == 0)
                return;
            groups.Add(new JsonObject
            {
                ["name"] = null,
                ["addresses"] = BuildAddressArray(ungrouped),
            });
            ungrouped.Clear();
        }
    }

    private static JsonArray BuildAddressArray(IEnumerable<MailboxAddress> mailboxes)
    {
        var result = new JsonArray();
        foreach (var mailbox in mailboxes)
            result.Add(BuildAddress(mailbox));
        return result;
    }

    private static JsonObject BuildAddress(MailboxAddress mailbox)
    {
        var name = NormalizeDecodedHeaderText(mailbox.Name);
        return new JsonObject
        {
            ["name"] = string.IsNullOrEmpty(name) ? null : name,
            ["email"] = mailbox.Address,
        };
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

    private static JsonNode? ParseMessageIds(string value)
    {
        var result = new JsonArray();
        foreach (Match match in MessageIdRegex().Matches(value))
            result.Add(match.Groups[1].Value);
        return result.Count == 0 ? null : result;
    }

    private static JsonNode? ParseUrls(string value)
    {
        var result = new JsonArray();
        foreach (Match match in UrlRegex().Matches(value))
            result.Add(match.Groups[1].Value);
        return result.Count == 0 ? null : result;
    }

    private static bool TryParseHeaderProperty(
        string property,
        out HeaderProperty parsed)
    {
        parsed = default;
        if (!property.StartsWith("header:", StringComparison.Ordinal))
            return false;
        var remainder = property[7..];
        var all = remainder.EndsWith(":all", StringComparison.Ordinal);
        if (all)
            remainder = remainder[..^4];

        var form = HeaderForm.Raw;
        var asIndex = remainder.LastIndexOf(":as", StringComparison.Ordinal);
        if (asIndex >= 0)
        {
            var formName = remainder[(asIndex + 3)..];
            remainder = remainder[..asIndex];
            if (!Enum.TryParse(formName, ignoreCase: false, out form))
                return false;
        }
        if (remainder.Length == 0
            || remainder.Any(character => character is < (char)33 or > (char)126 || character == ':')
            || !IsAllowedHeaderForm(remainder, form))
        {
            return false;
        }

        parsed = new HeaderProperty(remainder, form, all);
        return true;
    }

    private static bool IsAllowedHeaderForm(string name, HeaderForm form)
    {
        if (form == HeaderForm.Raw)
            return true;
        var normalized = name.ToUpperInvariant();
        return form switch
        {
            HeaderForm.Text => normalized is "SUBJECT" or "COMMENTS" or "KEYWORDS" or "LIST-ID"
                || !KnownHeaderNames.Contains(normalized),
            HeaderForm.Addresses or HeaderForm.GroupedAddresses => normalized is
                "FROM" or "SENDER" or "REPLY-TO" or "TO" or "CC" or "BCC"
                or "RESENT-FROM" or "RESENT-SENDER" or "RESENT-REPLY-TO"
                or "RESENT-TO" or "RESENT-CC" or "RESENT-BCC"
                || !KnownHeaderNames.Contains(normalized),
            HeaderForm.MessageIds => normalized is
                "MESSAGE-ID" or "IN-REPLY-TO" or "REFERENCES" or "RESENT-MESSAGE-ID"
                || !KnownHeaderNames.Contains(normalized),
            HeaderForm.Date => normalized is "DATE" or "RESENT-DATE"
                || !KnownHeaderNames.Contains(normalized),
            HeaderForm.URLs => normalized is
                "LIST-HELP" or "LIST-UNSUBSCRIBE" or "LIST-SUBSCRIBE" or "LIST-POST"
                or "LIST-OWNER" or "LIST-ARCHIVE"
                || !KnownHeaderNames.Contains(normalized),
            _ => false,
        };
    }

    private static readonly IReadOnlySet<string> KnownHeaderNames = new HashSet<string>(
        [
            "DATE", "FROM", "SENDER", "REPLY-TO", "TO", "CC", "BCC",
            "MESSAGE-ID", "IN-REPLY-TO", "REFERENCES", "SUBJECT", "COMMENTS",
            "KEYWORDS", "RESENT-DATE", "RESENT-FROM", "RESENT-SENDER",
            "RESENT-REPLY-TO", "RESENT-TO", "RESENT-CC", "RESENT-BCC", "RESENT-MESSAGE-ID",
            "RETURN-PATH", "RECEIVED", "MIME-VERSION", "CONTENT-TYPE",
            "CONTENT-TRANSFER-ENCODING", "CONTENT-ID", "CONTENT-DESCRIPTION",
            "CONTENT-DISPOSITION", "CONTENT-LANGUAGE", "CONTENT-LOCATION",
            "LIST-ID", "LIST-HELP", "LIST-UNSUBSCRIBE", "LIST-SUBSCRIBE",
            "LIST-POST", "LIST-OWNER", "LIST-ARCHIVE",
        ],
        StringComparer.Ordinal);

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

    [GeneratedRegex("<([^<>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex MessageIdRegex();

    [GeneratedRegex("<([^<>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRegex();

    private enum HeaderForm
    {
        Raw,
        Text,
        Addresses,
        GroupedAddresses,
        MessageIds,
        Date,
        URLs,
    }

    private readonly record struct HeaderProperty(string Name, HeaderForm Form, bool All);

    private sealed record PartDescriptor(
        MimeEntity Entity,
        string? PartId,
        string? BlobId,
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
