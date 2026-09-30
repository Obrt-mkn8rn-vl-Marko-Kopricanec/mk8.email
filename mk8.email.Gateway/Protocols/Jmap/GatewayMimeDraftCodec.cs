using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using MimeKit.Utils;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayMimeDraftCodec
{
    private const long MaximumUnsignedInt = 9_007_199_254_740_991;

    private static readonly HashSet<string> MetadataProperties = new HashSet<string>(
        ["mailboxIds", "keywords", "receivedAt"],
        StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ConvenienceHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["messageId"] = "Message-ID",
            ["inReplyTo"] = "In-Reply-To",
            ["references"] = "References",
            ["sender"] = "Sender",
            ["from"] = "From",
            ["to"] = "To",
            ["cc"] = "Cc",
            ["bcc"] = "Bcc",
            ["replyTo"] = "Reply-To",
            ["subject"] = "Subject",
            ["sentAt"] = "Date",
        };

    private static readonly HashSet<string> BodyProperties = new HashSet<string>(
        [
            "partId", "blobId", "size", "name", "type", "charset", "disposition",
            "cid", "language", "location", "subParts",
        ],
        StringComparer.Ordinal);

    public static Result Parse(JsonObject value)
    {
        var allowed = MetadataProperties.Concat(ConvenienceHeaders.Keys)
            .Concat(["bodyStructure", "bodyValues", "textBody", "htmlBody", "attachments"])
            .ToHashSet(StringComparer.Ordinal);
        var invalid = value.Select(item => item.Key)
            .Where(property => !allowed.Contains(property) && !property.StartsWith("header:", StringComparison.Ordinal))
            .ToArray();
        if (invalid.Length > 0 || value.ContainsKey("headers"))
            return Failed("invalidProperties", properties: invalid);
        using var message = new MimeMessage((IEnumerable<Header>)Array.Empty<Header>());
        if (!TryApplyHeaders(message, value, out var representedHeaders, out var headerError))
            return Failed("invalidProperties", headerError);
        var forbidden = representedHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        forbidden.UnionWith(["From", "Message-ID", "Date"]);
        var bodyValues = value["bodyValues"] as JsonObject;
        if (value.ContainsKey("bodyValues") && bodyValues is null
            || bodyValues is not null && !ValidateBodyValues(bodyValues))
            return Failed("invalidProperties", properties: ["bodyValues"]);
        var references = EnumerateBlobReferences(value).Distinct(StringComparer.Ordinal).ToArray();
        if (references.Any(reference => !IsReference(reference)))
            return Failed("invalidProperties", properties: ["blobId"]);
        using var sourceReferences = new DraftPartScope();
        var partIds = new HashSet<string>(StringComparer.Ordinal);
        PartBuildResult built;
        if (value.TryGetPropertyValue("bodyStructure", out var node))
        {
            built = node is JsonObject structure && !value.ContainsKey("textBody")
                && !value.ContainsKey("htmlBody") && !value.ContainsKey("attachments")
                ? BuildPart(structure, bodyValues, partIds, sourceReferences, forbidden)
                : PartBuildResult.Failed("invalidProperties", properties: ["bodyStructure"]);
        }
        else built = BuildFlatBody(value, bodyValues, partIds, sourceReferences);
        if (built.Error is not null)
            return new(null, ConvertFailure(built.Error), references, true);
        message.Body = built.Entity ?? sourceReferences.Create(() => new TextPart("plain") { Text = string.Empty });
        sourceReferences.TransferToMessage(message.Body);
        var rows = new List<MailMimeDraftPart>();
        var root = Capture(message.Body, rows, sourceReferences);
        return new(new(CaptureHeaders(message.Headers), rows.ToArray(), root), null, references, true);
    }

    private static int Capture(MimeEntity entity, List<MailMimeDraftPart> rows,
        DraftPartScope references)
    {
        var children = entity is Multipart multipart
            ? multipart.Select(child => Capture(child, rows, references)).ToArray() : [];
        var index = rows.Count;
        rows.Add(new(entity.ContentType.MimeType, CaptureHeaders(entity.Headers),
            entity is TextPart text ? text.Text : null, references.Reference(entity), children));
        return index;
    }

    private static MailMimeHeaderSnapshot[] CaptureHeaders(HeaderList headers) =>
        headers.Select(header => new MailMimeHeaderSnapshot(header.RawField.ToArray(), header.RawValue.ToArray())).ToArray();

    internal sealed record Result(MailMimeDraft? Mime, MailMessageMutationFailure? Failure,
        IReadOnlyList<string> BlobReferences, bool CheckBlobsBeforeFailure);

    private static Result Failed(string type, string? description = null, string[]? properties = null) =>
        new(null, ConvertFailure(SetError(type, description, properties)), [], false);

    private static MailMessageMutationFailure ConvertFailure(JsonObject value) =>
        new(string.Equals(value["type"]!.GetValue<string>(), "invalidEmail", StringComparison.Ordinal)
            ? MailMessageMutationError.InvalidEmail : MailMessageMutationError.InvalidProperties,
            value["description"]?.GetValue<string>(),
            value["properties"] is JsonArray list ? list.Select(item => item!.GetValue<string>()).ToArray() : null, null);

    private static bool IsReference(string value) =>
        GatewayJmapBatchCodec.IsId(value) || value.StartsWith('#') && GatewayJmapBatchCodec.IsId(value[1..]);

    private static PartBuildResult BuildFlatBody(
        JsonObject value, JsonObject? bodyValues, ISet<string> partIds,
        DraftPartScope sourceReferences)
    {
        if (!TryGetPartArray(value, "textBody", out var textParts)
            || !TryGetPartArray(value, "htmlBody", out var htmlParts)
            || !TryGetPartArray(value, "attachments", out var attachmentParts)
            || textParts is { Count: not 1 }
            || htmlParts is { Count: not 1 })
        {
            return PartBuildResult.Failed("invalidProperties");
        }

        MimeEntity? text = null;
        MimeEntity? html = null;
        if (textParts is { Count: 1 })
        {
            if (!HasType(textParts[0], "text/plain"))
                return PartBuildResult.Failed("invalidProperties", properties: ["textBody"]);
            var built = BuildPart(textParts[0], bodyValues, partIds, sourceReferences);
            if (built.Error is not null) return built;
            text = built.Entity;
        }
        if (htmlParts is { Count: 1 })
        {
            if (!HasType(htmlParts[0], "text/html"))
                return PartBuildResult.Failed("invalidProperties", properties: ["htmlBody"]);
            var built = BuildPart(htmlParts[0], bodyValues, partIds, sourceReferences);
            if (built.Error is not null) return built;
            html = built.Entity;
        }

        MimeEntity? body = null;
        if (text is not null && html is not null)
        {
            var alternative = sourceReferences.Create(() => new Multipart("alternative"));
            sourceReferences.Attach(alternative, text);
            sourceReferences.Attach(alternative, html);
            body = alternative;
        }
        else
        {
            body = text ?? html;
        }

        if (attachmentParts is { Count: > 0 })
        {
            var mixed = sourceReferences.Create(() => new Multipart("mixed"));
            if (body is not null)
                sourceReferences.Attach(mixed, body);
            foreach (var attachment in attachmentParts)
            {
                var built = BuildPart(attachment, bodyValues, partIds, sourceReferences);
                if (built.Error is not null) return built;
                sourceReferences.Attach(mixed, built.Entity!);
            }
            body = mixed;
        }
        return new PartBuildResult(body, null);
    }

    private static PartBuildResult BuildPart(
        JsonObject value, JsonObject? bodyValues, ISet<string> partIds,
        DraftPartScope sourceReferences, IReadOnlySet<string>? forbiddenHeaders = null)
    {
        var invalid = value.Select(item => item.Key)
            .Where(property => !BodyProperties.Contains(property)
                && !property.StartsWith("header:", StringComparison.Ordinal))
            .ToArray();
        if (invalid.Length > 0 || value.ContainsKey("headers"))
            return PartBuildResult.Failed("invalidProperties", properties: invalid);
        if (!TryGetRequiredString(value, "type", out var type)
            || !TryMimeType(type, out var mediaType, out var mediaSubtype)
            || !TryGetOptionalString(value, "partId", out var partId)
            || !TryGetOptionalString(value, "blobId", out var blobId)
            || !TryGetOptionalString(value, "charset", out var charset)
            || !TryGetOptionalString(value, "name", out var name)
            || !TryGetOptionalString(value, "disposition", out var disposition)
            || !TryGetOptionalString(value, "cid", out var contentId)
            || !TryGetOptionalString(value, "location", out var location)
            || partId is not null && blobId is not null
            || !TryValidateOptionalSize(value))
        {
            return PartBuildResult.Failed("invalidProperties");
        }
        if (!string.Equals(mediaType, "text", StringComparison.Ordinal) && charset is not null)
            return PartBuildResult.Failed("invalidProperties", properties: ["charset"]);

        var metadata = new PartMetadata(name, disposition, contentId, location, forbiddenHeaders);
        if (string.Equals(mediaType, "multipart", StringComparison.Ordinal))
        {
            if (partId is not null || blobId is not null || value["subParts"] is not JsonArray subParts)
                return PartBuildResult.Failed("invalidProperties");
            return BuildMultipart(value, subParts, mediaSubtype, bodyValues, partIds, sourceReferences, metadata);
        }
        if (value.TryGetPropertyValue("subParts", out var subPartsNode) && subPartsNode is not null)
            return PartBuildResult.Failed("invalidProperties", properties: ["subParts"]);
        return BuildLeaf(value, bodyValues, partIds, sourceReferences,
            new(mediaType, mediaSubtype, partId, blobId, charset), metadata);
    }

    private static PartBuildResult BuildMultipart(JsonObject value, JsonArray subParts, string mediaSubtype,
        JsonObject? bodyValues, ISet<string> partIds, DraftPartScope sourceReferences, PartMetadata metadata)
    {
        var multipart = sourceReferences.Create(() => new Multipart(mediaSubtype));
        foreach (var item in subParts)
        {
            if (item is not JsonObject child)
                return PartBuildResult.Failed("invalidProperties");
            var built = BuildPart(child, bodyValues, partIds, sourceReferences);
            if (built.Error is not null)
                return built;
            sourceReferences.Attach(multipart, built.Entity!);
        }
        if (!TryApplyPartMetadata(multipart, value, metadata))
        {
            return PartBuildResult.Failed("invalidProperties");
        }
        return new PartBuildResult(multipart, null);
    }

    private static PartBuildResult BuildLeaf(JsonObject value, JsonObject? bodyValues, ISet<string> partIds,
        DraftPartScope sourceReferences, LeafDefinition definition, PartMetadata metadata)
    {
        var (mediaType, mediaSubtype, partId, blobId, charset) = definition;
        MimePart part;
        if (partId is not null)
        {
            if (value.ContainsKey("charset")
                || value.ContainsKey("size")
                || !TryGetBodyValue(bodyValues, partId, out var textValue)
                || !string.Equals(mediaType, "text", StringComparison.Ordinal)
                || !partIds.Add(partId))
            {
                return PartBuildResult.Failed("invalidProperties");
            }
            part = sourceReferences.Create(() => new TextPart(mediaSubtype)
            {
                Text = textValue,
                ContentTransferEncoding = ContentEncoding.QuotedPrintable,
            });
        }
        else if (blobId is not null)
        {
            part = sourceReferences.Create(() => new MimePart(mediaType, mediaSubtype)
            {
                ContentTransferEncoding = string.Equals(mediaType, "text", StringComparison.Ordinal)
                    ? ContentEncoding.QuotedPrintable : ContentEncoding.Base64,
            });
            sourceReferences.AddReference(part, blobId);
        }
        else
        {
            return PartBuildResult.Failed("invalidProperties");
        }

        if (string.Equals(mediaType, "text", StringComparison.Ordinal) && charset is not null)
        {
            try
            {
                _ = Encoding.GetEncoding(charset);
                part.ContentType.Charset = charset;
            }
            catch (ArgumentException)
            {
                return PartBuildResult.Failed("invalidProperties", properties: ["charset"]);
            }
        }
        if (!TryApplyPartMetadata(part, value, metadata))
        {
            return PartBuildResult.Failed("invalidProperties");
        }
        return new PartBuildResult(part, null);
    }

    private static bool TryApplyHeaders(
        MimeMessage message,
        JsonObject value,
        out HashSet<string> representedHeaders,
        out string? error)
    {
        error = null;
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        representedHeaders = represented;
        foreach (var item in value)
        {
            if (ConvenienceHeaders.TryGetValue(item.Key, out var headerName))
            {
                if (!represented.Add(headerName)
                    || !TryApplyConvenienceHeader(message, item.Key, item.Value))
                {
                    error = $"The {item.Key} property is invalid or duplicated.";
                    return false;
                }
            }
            else if (item.Key.StartsWith("header:", StringComparison.Ordinal))
            {
                if (!TryParseWritableHeaderProperty(item.Key, out var header)
                    || header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                    || !represented.Add(header.Name)
                    || !TryAddHeaderValues(message.Headers, header, item.Value))
                {
                    error = $"The {item.Key} property is invalid or duplicated.";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryApplyConvenienceHeader(
        MimeMessage message,
        string property,
        JsonNode? node)
    {
        switch (property)
        {
            case "from": return TrySetAddressList(message.Headers, HeaderId.From, node);
            case "to": return TrySetAddressList(message.Headers, HeaderId.To, node);
            case "cc": return TrySetAddressList(message.Headers, HeaderId.Cc, node);
            case "bcc": return TrySetAddressList(message.Headers, HeaderId.Bcc, node);
            case "replyTo": return TrySetAddressList(message.Headers, HeaderId.ReplyTo, node);
            case "sender": return TrySetAddressList(message.Headers, HeaderId.Sender, node);
            case "subject":
                if (node is null) return true;
                if (node is not JsonValue subjectValue
                    || !subjectValue.TryGetValue<string>(out var subject)) return false;
                message.Subject = subject;
                return true;
            case "sentAt":
                if (node is null) return true;
                if (node is not JsonValue dateValue
                    || !dateValue.TryGetValue<string>(out var dateText)
                    || !GatewayDraftDateCodec.TryParseDate(dateText, out var date))
                    return false;
                message.Date = date;
                return true;
            case "messageId":
                if (node is null)
                    return true;
                return TrySetMessageIds(node, ids =>
                {
                    message.Headers.Add(
                        HeaderId.MessageId,
                        string.Join(' ', ids.Select(id => $"<{id}>")));
                    return true;
                });
            case "inReplyTo":
                if (node is null)
                    return true;
                return TrySetMessageIds(node, ids =>
                {
                    message.Headers.Add(
                        HeaderId.InReplyTo,
                        string.Join(' ', ids.Select(id => $"<{id}>")));
                    return true;
                });
            case "references":
                if (node is null)
                    return true;
                return TrySetMessageIds(node, ids =>
                {
                    message.Headers.Add(
                        HeaderId.References,
                        string.Join(' ', ids.Select(id => $"<{id}>")));
                    return true;
                });
            default:
                return false;
        }
    }

    private static bool TrySetAddressList(
        HeaderList headers,
        HeaderId headerId,
        JsonNode? node)
    {
        if (node is null)
            return true;
        if (!TryParseAddressList(node, out var addresses))
            return false;
        headers.Add(headerId, addresses.ToString());
        return true;
    }

    private static bool TryParseAddressList(JsonNode? node, out InternetAddressList result)
    {
        result = [];
        if (node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        foreach (var item in array)
        {
            if (item is not JsonObject address
                || !TryGetRequiredString(address, "email", out var email)
                || !TryGetOptionalString(address, "name", out var name)
                || address.Any(property => property.Key is not ("email" or "name")))
            {
                return false;
            }
            try
            {
                result.Add(new MailboxAddress(name ?? string.Empty, email));
            }
            catch (ParseException)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TrySetMessageIds(
        JsonNode? node,
        Func<IReadOnlyList<string>, bool> setter)
    {
        if (node is null)
            return setter([]);
        if (node is not JsonArray array)
            return false;
        var ids = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var id)
                || !GatewayMessageId.TryParseParsedForm(id, out var parsed))
            {
                return false;
            }
            ids.Add(parsed);
        }
        return setter(ids.ToArray());
    }

    private static bool TryGetPartArray(
        JsonObject value,
        string name,
        out IReadOnlyList<JsonObject>? parts)
    {
        parts = null;
        if (!value.TryGetPropertyValue(name, out var node))
            return true;
        if (node is not JsonArray array)
            return false;
        var result = new List<JsonObject>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject part)
                return false;
            result.Add(part);
        }
        parts = result.ToArray();
        return true;
    }

    private static bool HasType(JsonObject part, string type) =>
        part["type"] is JsonValue value
        && value.TryGetValue<string>(out var parsed)
        && string.Equals(parsed, type, StringComparison.OrdinalIgnoreCase);

    private static bool TryMimeType(
        string value,
        out string mediaType,
        out string mediaSubtype)
    {
        mediaType = string.Empty;
        mediaSubtype = string.Empty;
        if (!GatewayMediaType.TryNormalize(value, out var normalized))
            return false;
        var separator = normalized.IndexOf('/', StringComparison.Ordinal);
        mediaType = normalized[..separator];
        mediaSubtype = normalized[(separator + 1)..];
        return true;
    }

    private static bool IsMimeToken(string value) =>
        value.Length is > 0 and <= 127
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '!' or '#' or '$' or '&' or '-' or '^' or '_' or '.' or '+');

    private static bool TryGetBodyValue(
        JsonObject? bodyValues,
        string partId,
        out string text)
    {
        text = string.Empty;
        if (bodyValues?[partId] is not JsonObject bodyValue
            || !TryGetRequiredString(bodyValue, "value", out text)
            || !TryGetOptionalBoolean(bodyValue, "isEncodingProblem", false, out var encodingProblem)
            || !TryGetOptionalBoolean(bodyValue, "isTruncated", false, out var truncated)
            || bodyValue.Any(item => item.Key is not ("value" or "isEncodingProblem" or "isTruncated"))
            || encodingProblem
            || truncated)
        {
            return false;
        }
        return true;
    }

    private static bool TryApplyPartMetadata(MimeEntity entity, JsonObject source, PartMetadata metadata)
    {
        if (!TryAssignMetadata(entity, metadata) || !TryAssignLanguages(entity, source)) return false;
        var representedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type",
        };
        if (source.ContainsKey("name") || source.ContainsKey("disposition"))
            representedHeaders.Add("Content-Disposition");
        if (source.ContainsKey("cid"))
            representedHeaders.Add("Content-ID");
        if (source.ContainsKey("language"))
            representedHeaders.Add("Content-Language");
        if (source.ContainsKey("location"))
            representedHeaders.Add("Content-Location");


        foreach (var item in source.Where(item => item.Key.StartsWith("header:", StringComparison.Ordinal)))
        {
            if (!TryParseWritableHeaderProperty(item.Key, out var header)
                || header.Name.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || representedHeaders.Contains(header.Name)
                || metadata.ForbiddenHeaders?.Contains(header.Name) == true
                || !representedHeaders.Add(header.Name)
                || !TryAddHeaderValues(entity.Headers, header, item.Value))
            {
                return false;
            }
        }
        return true;
    }


    private static bool TryAssignMetadata(MimeEntity entity, PartMetadata metadata)
    {
        var (name, disposition, contentId, location, _) = metadata;
        if (name is not null && entity is MimePart mimePart)
        {
            try
            {
                mimePart.FileName = name;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                return false;
            }
        }
        else if (name is not null)
        {
            try
            {
                entity.ContentType.Name = name;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                return false;
            }
        }
        try
        {
            if (disposition is not null)
            {
                if (!IsMimeToken(CultureInfo.InvariantCulture.TextInfo.ToLower(disposition)))
                    return false;
                entity.ContentDisposition = new ContentDisposition(CultureInfo.InvariantCulture.TextInfo.ToLower(disposition));
            }
            if (contentId is not null)
            {
                var normalizedContentId = contentId.Trim('<', '>');
                if (normalizedContentId.Length == 0)
                    return false;
                entity.ContentId = normalizedContentId;
            }
            if (location is not null)
            {
                if (!Uri.TryCreate(location, UriKind.RelativeOrAbsolute, out var uri))
                    return false;
                entity.ContentLocation = uri;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }

        return true;
    }

    private static bool TryAssignLanguages(MimeEntity entity, JsonObject source)
    {
        if (source.TryGetPropertyValue("language", out var languageNode))
        {
            if (languageNode is not null && languageNode is not JsonArray)
                return false;
            if (languageNode is JsonArray languages)
            {
                var values = new List<string>();
                foreach (var item in languages)
                {
                    if (item is not JsonValue value
                        || !value.TryGetValue<string>(out var language)
                        || !GatewayLanguageTag.IsValid(language))
                    {
                        return false;
                    }
                    values.Add(language);
                }
                if (values.Count > 0)
                    entity.Headers["Content-Language"] = string.Join(", ", values);
            }
        }


        return true;
    }

    private static bool TryValidateOptionalSize(JsonObject value)
    {
        if (!value.TryGetPropertyValue("size", out var node))
            return true;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue<long>(out var size)
            && size is >= 0 and <= MaximumUnsignedInt;
    }

    private static bool ValidateBodyValues(JsonObject bodyValues)
    {
        foreach (var item in bodyValues)
        {
            if (item.Value is not JsonObject bodyValue
                || !TryGetRequiredString(bodyValue, "value", out _)
                || !TryGetOptionalBoolean(
                    bodyValue,
                    "isEncodingProblem",
                    false,
                    out var encodingProblem)
                || !TryGetOptionalBoolean(
                    bodyValue,
                    "isTruncated",
                    false,
                    out var truncated)
                || encodingProblem
                || truncated
                || bodyValue.Any(property =>
                    property.Key is not ("value" or "isEncodingProblem" or "isTruncated")))
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> EnumerateBlobReferences(JsonObject email)
    {
        if (email["bodyStructure"] is JsonObject bodyStructure)
        {
            foreach (var reference in EnumeratePartBlobReferences(bodyStructure))
                yield return reference;
            yield break;
        }

        foreach (var property in new[] { "textBody", "htmlBody", "attachments" })
        {
            if (email[property] is not JsonArray parts)
                continue;
            foreach (var item in parts)
            {
                if (item is not JsonObject part)
                    continue;
                foreach (var reference in EnumeratePartBlobReferences(part))
                    yield return reference;
            }
        }
    }

    private static IEnumerable<string> EnumeratePartBlobReferences(JsonObject part)
    {
        if (part["blobId"] is JsonValue value && value.TryGetValue<string>(out var blobId))
            yield return blobId;
        if (part["subParts"] is not JsonArray subParts)
            yield break;
        foreach (var item in subParts)
        {
            if (item is not JsonObject child)
                continue;
            foreach (var reference in EnumeratePartBlobReferences(child))
                yield return reference;
        }
    }

    private static bool TryParseWritableHeaderProperty(
        string property,
        out WritableHeader header)
    {
        header = default;
        if (!property.StartsWith("header:", StringComparison.Ordinal))
            return false;
        var remainder = property[7..];
        var all = remainder.EndsWith(":all", StringComparison.Ordinal);
        if (all) remainder = remainder[..^4];
        var form = "Raw";
        var asIndex = remainder.LastIndexOf(":as", StringComparison.Ordinal);
        if (asIndex >= 0)
        {
            form = remainder[(asIndex + 3)..];
            remainder = remainder[..asIndex];
        }
        if (remainder.Length == 0
            || remainder.Any(character => character is < (char)33 or > (char)126 || character == ':')
            || form is not ("Raw" or "Text" or "Addresses" or "GroupedAddresses" or "MessageIds" or "Date" or "URLs")
            || !IsAllowedHeaderForm(remainder, form))
        {
            return false;
        }
        header = new WritableHeader(remainder, form, all);
        return true;
    }

    private static bool TryAddHeaderValues(
        HeaderList headers,
        WritableHeader header,
        JsonNode? node)
    {
        try
        {
            if (header.All)
            {
                if (node is not JsonArray array)
                    return false;
                foreach (var item in array)
                {
                    if (!TryAddHeaderValue(headers, header, item))
                        return false;
                }
                return true;
            }
            if (node is null)
                return true;
            return TryAddHeaderValue(headers, header, node);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool TryAddHeaderValue(
        HeaderList headers,
        WritableHeader header,
        JsonNode? node)
    {
        if (string.Equals(header.Form, "Raw", StringComparison.Ordinal))
        {
            if (node is not JsonValue rawNode
                || !rawNode.TryGetValue<string>(out var rawValue)
                || !IsValidRawHeaderValue(header.Name, rawValue))
            {
                return false;
            }

            var rawHeader = new Header(header.Name, string.Empty);
            rawHeader.SetRawValue(Encoding.UTF8.GetBytes(rawValue + "\r\n"));
            headers.Add(rawHeader);
            return true;
        }

        if (!TryFormatHeaderValue(header.Form, node, out var value))
            return false;
        headers.Add(header.Name, value);
        return true;
    }

    private static bool IsValidRawHeaderValue(string name, string value)
    {
        var lineLength = checked(name.Length + 1);
        for (var index = 0; index < value.Length;)
        {
            var character = value[index];
            if (character == '\r')
            {
                if (index + 2 >= value.Length
                    || value[index + 1] != '\n'
                    || value[index + 2] is not (' ' or '\t')
                    || lineLength > 998)
                {
                    return false;
                }
                lineLength = 0;
                index += 2;
                continue;
            }
            if (character == '\n'
                || character == '\0'
                || character is < (char)0x20 and not '\t'
                || character == (char)0x7f)
            {
                return false;
            }

            if (Rune.TryGetRuneAt(value, index, out var rune))
            {
                lineLength = checked(lineLength + rune.Utf8SequenceLength);
                index += rune.Utf16SequenceLength;
            }
            else
            {
                return false;
            }
        }
        return lineLength <= 998;
    }

    private static bool IsAllowedHeaderForm(string name, string form)
    {
        if (string.Equals(form, "Raw", StringComparison.Ordinal))
            return true;
        var normalized = name.ToUpperInvariant();
        return form switch
        {
            "Text" => normalized is "SUBJECT" or "COMMENTS" or "KEYWORDS" or "LIST-ID"
                || !KnownHeaderNames.Contains(normalized),
            "Addresses" or "GroupedAddresses" => normalized is
                "FROM" or "SENDER" or "REPLY-TO" or "TO" or "CC" or "BCC"
                or "RESENT-FROM" or "RESENT-SENDER" or "RESENT-REPLY-TO"
                or "RESENT-TO" or "RESENT-CC" or "RESENT-BCC"
                || !KnownHeaderNames.Contains(normalized),
            "MessageIds" => normalized is
                "MESSAGE-ID" or "IN-REPLY-TO" or "REFERENCES" or "RESENT-MESSAGE-ID"
                || !KnownHeaderNames.Contains(normalized),
            "Date" => normalized is "DATE" or "RESENT-DATE"
                || !KnownHeaderNames.Contains(normalized),
            "URLs" => normalized is
                "LIST-HELP" or "LIST-UNSUBSCRIBE" or "LIST-SUBSCRIBE" or "LIST-POST"
                or "LIST-OWNER" or "LIST-ARCHIVE"
                || !KnownHeaderNames.Contains(normalized),
            _ => false,
        };
    }

    private static readonly HashSet<string> KnownHeaderNames = new HashSet<string>(
        [
            "DATE", "FROM", "SENDER", "REPLY-TO", "TO", "CC", "BCC",
            "MESSAGE-ID", "IN-REPLY-TO", "REFERENCES", "SUBJECT", "COMMENTS",
            "KEYWORDS", "RESENT-DATE", "RESENT-FROM", "RESENT-SENDER",
            "RESENT-REPLY-TO", "RESENT-TO", "RESENT-CC", "RESENT-BCC",
            "RESENT-MESSAGE-ID", "RETURN-PATH", "RECEIVED",
            "LIST-HELP", "LIST-UNSUBSCRIBE", "LIST-SUBSCRIBE", "LIST-POST",
            "LIST-OWNER", "LIST-ARCHIVE",
        ],
        StringComparer.Ordinal);

    private static bool TryFormatHeaderValue(
        string form,
        JsonNode? node,
        out string value)
    {
        value = string.Empty;
        if (string.Equals(form, "Text", StringComparison.Ordinal))
        {
            return node is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out value!);
        }
        if (string.Equals(form, "Addresses", StringComparison.Ordinal))
        {
            if (!TryParseAddressList(node, out var addresses)) return false;
            value = addresses.ToString();
            return true;
        }
        if (string.Equals(form, "GroupedAddresses", StringComparison.Ordinal))
            return TryFormatGroupedAddresses(node, out value);
        if (string.Equals(form, "MessageIds", StringComparison.Ordinal))
        {
            if (node is not JsonArray ids) return false;
            var parsed = new List<string>();
            foreach (var item in ids)
            {
                if (item is not JsonValue idValue
                    || !idValue.TryGetValue<string>(out var id)
                    || !GatewayMessageId.TryParseParsedForm(id, out var parsedId)) return false;
                parsed.Add($"<{parsedId}>");
            }
            value = string.Join(' ', parsed);
            return true;
        }
        if (string.Equals(form, "Date", StringComparison.Ordinal))
        {
            if (node is not JsonValue dateValue
                || !dateValue.TryGetValue<string>(out var text)
                || !GatewayDraftDateCodec.TryParseDate(text, out var date)) return false;
            value = DateUtils.FormatDate(date);
            return true;
        }
        if (string.Equals(form, "URLs", StringComparison.Ordinal))
        {
            if (node is not JsonArray urls) return false;
            var parsed = new List<string>();
            foreach (var item in urls)
            {
                if (item is not JsonValue urlValue
                    || !urlValue.TryGetValue<string>(out var url)
                    || !GatewayHeaderUrl.IsValidParsedForm(url)) return false;
                parsed.Add($"<{url}>");
            }
            value = string.Join(", ", parsed);
            return true;
        }
        return false;
    }

    private static bool TryFormatGroupedAddresses(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonArray groups) return false;
        var addresses = new InternetAddressList();
        foreach (var item in groups)
        {
            if (item is not JsonObject group
                || !TryGetOptionalString(group, "name", out var name)
                || !group.TryGetPropertyValue("addresses", out var addressNode)
                || addressNode is null
                || !TryParseAddressList(addressNode, out var members)
                || group.Any(property => property.Key is not ("name" or "addresses"))) return false;
            if (name is null) addresses.AddRange(members);
            else addresses.Add(new GroupAddress(name, members));
        }
        value = addresses.ToString();
        return true;
    }

    private static bool TryGetRequiredString(JsonObject value, string key, out string result)
    {
        result = string.Empty;
        return value[key] is JsonValue node && node.TryGetValue(out result!) && result is not null;
    }

    private static bool TryGetOptionalString(JsonObject value, string key, out string? result)
    {
        result = null;
        return !value.TryGetPropertyValue(key, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue(out result);
    }

    private static bool TryGetOptionalBoolean(JsonObject value, string key, bool fallback, out bool result)
    {
        result = fallback;
        return !value.TryGetPropertyValue(key, out var node)
            || node is JsonValue scalar && scalar.TryGetValue(out result);
    }

    private static JsonObject SetError(string type, string? description = null, IEnumerable<string>? properties = null)
    {
        var result = new JsonObject { ["type"] = type };
        if (description is not null) result["description"] = description;
        if (properties is not null) result["properties"] = new JsonArray(properties.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
        return result;
    }

    private sealed class DraftPartScope : IDisposable
    {
        private readonly HashSet<MimeEntity> _roots = [];
        private readonly Dictionary<MimeEntity, string> _references = [];

        public T Create<T>(Func<T> create) where T : MimeEntity
        {
            var entity = create();
            _roots.Add(entity);
            return entity;
        }

        public void Attach(Multipart parent, MimeEntity child)
        {
            parent.Add(child);
            _roots.Remove(child);
        }

        public void TransferToMessage(MimeEntity entity) => _roots.Remove(entity);
        public void AddReference(MimeEntity entity, string reference) => _references.Add(entity, reference);
        public string? Reference(MimeEntity entity) => _references.GetValueOrDefault(entity);

        public void Dispose()
        {
            foreach (var root in _roots) root.Dispose();
            _roots.Clear();
        }
    }

    private sealed record PartMetadata(string? Name, string? Disposition, string? ContentId,
        string? Location, IReadOnlySet<string>? ForbiddenHeaders);
    private sealed record LeafDefinition(string MediaType, string MediaSubtype, string? PartId,
        string? BlobId, string? Charset);

    private readonly record struct WritableHeader(string Name, string Form, bool All);

    private sealed record PartBuildResult(MimeEntity? Entity, JsonObject? Error)
    {
        public static PartBuildResult Failed(
            string type,
            string? description = null,
            IEnumerable<string>? properties = null) =>
            new(null, SetError(type, description, properties));
    }
}
