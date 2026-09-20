using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using MimeKit.Utils;

namespace mk8.email.Jmap;

internal sealed record JmapBuiltMessage(
    MimeMessage Message,
    byte[] RawBytes,
    string Sender,
    string Recipient,
    string? Cc,
    string Subject,
    string MessageId,
    string? InReplyTo);

internal sealed record JmapBuildResult(JmapBuiltMessage? Value, JsonObject? Error)
{
    public static JmapBuildResult Failed(
        string type,
        string? description = null,
        IEnumerable<string>? properties = null) =>
        new(null, JmapMethodHelpers.SetError(type, description, properties));
}

internal sealed class JmapEmailBuilder(JmapBlobService blobs)
{
    private const long MaximumUnsignedInt = 9_007_199_254_740_991;

    private static readonly IReadOnlySet<string> MetadataProperties = new HashSet<string>(
        ["mailboxIds", "keywords", "receivedAt"],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> ConvenienceHeaders =
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

    private static readonly IReadOnlySet<string> BodyProperties = new HashSet<string>(
        [
            "partId", "blobId", "size", "name", "type", "charset", "disposition",
            "cid", "language", "location", "subParts",
        ],
        StringComparer.Ordinal);

    public async Task<JmapBuildResult> BuildAsync(
        Guid accountId,
        string accountAddress,
        JmapInvocationContext context,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        var allowed = MetadataProperties
            .Concat(ConvenienceHeaders.Keys)
            .Concat(["bodyStructure", "bodyValues", "textBody", "htmlBody", "attachments"])
            .ToHashSet(StringComparer.Ordinal);
        var invalid = value.Select(item => item.Key)
            .Where(property => !allowed.Contains(property)
                && !property.StartsWith("header:", StringComparison.Ordinal))
            .ToArray();
        if (invalid.Length > 0 || value.ContainsKey("headers"))
            return JmapBuildResult.Failed("invalidProperties", properties: invalid);

        var message = new MimeMessage();
        if (!TryApplyHeaders(message, value, out var representedHeaders, out var headerError))
        {
            message.Dispose();
            return JmapBuildResult.Failed("invalidProperties", headerError);
        }
        if (message.From.Count == 0)
            message.From.Add(MailboxAddress.Parse(accountAddress));
        if (string.IsNullOrEmpty(message.MessageId))
        {
            var domain = accountAddress[(accountAddress.LastIndexOf('@') + 1)..];
            message.MessageId = MimeUtils.GenerateMessageId(domain);
        }
        if (!message.Headers.Contains(HeaderId.Date))
            message.Date = DateTimeOffset.UtcNow;
        var rootForbiddenHeaders = representedHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        rootForbiddenHeaders.Add("From");
        rootForbiddenHeaders.Add("Message-ID");
        rootForbiddenHeaders.Add("Date");

        var bodyValues = value["bodyValues"] as JsonObject;
        if (value.ContainsKey("bodyValues") && bodyValues is null)
        {
            message.Dispose();
            return JmapBuildResult.Failed("invalidProperties", properties: ["bodyValues"]);
        }
        if (bodyValues is not null && !ValidateBodyValues(bodyValues))
        {
            message.Dispose();
            return JmapBuildResult.Failed("invalidProperties", properties: ["bodyValues"]);
        }

        var blobReferences = EnumerateBlobReferences(value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unresolvedReference = blobReferences.FirstOrDefault(reference => context.ResolveId(reference) is null);
        if (unresolvedReference is not null)
        {
            message.Dispose();
            return JmapBuildResult.Failed("invalidProperties", properties: ["blobId"]);
        }
        var missingBlobs = new List<string>();
        foreach (var reference in blobReferences)
        {
            var resolved = context.ResolveId(reference)!;
            if (await blobs.GetAsync(accountId, resolved, cancellationToken) is null)
                missingBlobs.Add(resolved);
        }
        if (missingBlobs.Count > 0)
        {
            message.Dispose();
            var error = JmapMethodHelpers.SetError("blobNotFound");
            error["notFound"] = JmapMethodHelpers.ToJsonArray(missingBlobs);
            return new JmapBuildResult(null, error);
        }

        MimeEntity? body;
        if (value.TryGetPropertyValue("bodyStructure", out var bodyStructureNode))
        {
            if (bodyStructureNode is not JsonObject bodyStructure
                || value.ContainsKey("textBody")
                || value.ContainsKey("htmlBody")
                || value.ContainsKey("attachments"))
            {
                message.Dispose();
                return JmapBuildResult.Failed("invalidProperties", properties: ["bodyStructure"]);
            }
            var built = await BuildPartAsync(
                accountId,
                context,
                bodyStructure,
                bodyValues,
                cancellationToken,
                rootForbiddenHeaders);
            if (built.Error is not null)
            {
                message.Dispose();
                return new JmapBuildResult(null, built.Error);
            }
            body = built.Entity;
        }
        else
        {
            var flat = await BuildFlatBodyAsync(
                accountId,
                context,
                value,
                bodyValues,
                cancellationToken);
            if (flat.Error is not null)
            {
                message.Dispose();
                return new JmapBuildResult(null, flat.Error);
            }
            body = flat.Entity;
        }
        message.Body = body ?? new TextPart("plain") { Text = string.Empty };

        byte[] raw;
        try
        {
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            using var stream = new MemoryStream();
            message.WriteTo(format, stream, cancellationToken);
            raw = stream.ToArray();
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            message.Dispose();
            return JmapBuildResult.Failed("invalidEmail", exception.Message);
        }

        var sender = message.From.Mailboxes.FirstOrDefault()?.Address ?? accountAddress;
        var recipients = message.To.Mailboxes
            .Concat(message.Cc.Mailboxes)
            .Concat(message.Bcc.Mailboxes)
            .Select(mailbox => mailbox.Address)
            .ToArray();
        var recipient = string.Join(", ", recipients);
        if (recipient.Length > 255)
            recipient = recipient[..255];
        var cc = message.Cc.ToString();
        if (cc.Length > 255)
            cc = cc[..255];
        var subject = message.Subject ?? string.Empty;
        if (subject.Length > 998)
            subject = subject[..998];
        var messageId = $"<{message.MessageId}>";
        var inReplyTo = message.InReplyTo;
        if (!string.IsNullOrEmpty(inReplyTo))
            inReplyTo = $"<{inReplyTo.Trim('<', '>')}>";
        return new JmapBuildResult(new JmapBuiltMessage(
            message,
            raw,
            sender,
            recipient,
            string.IsNullOrEmpty(cc) ? null : cc,
            subject,
            messageId,
            inReplyTo), null);
    }

    private async Task<PartBuildResult> BuildFlatBodyAsync(
        Guid accountId,
        JmapInvocationContext context,
        JsonObject value,
        JsonObject? bodyValues,
        CancellationToken cancellationToken)
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
            var built = await BuildPartAsync(accountId, context, textParts[0], bodyValues, cancellationToken);
            if (built.Error is not null) return built;
            text = built.Entity;
        }
        if (htmlParts is { Count: 1 })
        {
            if (!HasType(htmlParts[0], "text/html"))
                return PartBuildResult.Failed("invalidProperties", properties: ["htmlBody"]);
            var built = await BuildPartAsync(accountId, context, htmlParts[0], bodyValues, cancellationToken);
            if (built.Error is not null) return built;
            html = built.Entity;
        }

        MimeEntity? body = null;
        if (text is not null && html is not null)
        {
            var alternative = new Multipart("alternative") { text, html };
            body = alternative;
        }
        else
        {
            body = text ?? html;
        }

        if (attachmentParts is { Count: > 0 })
        {
            var mixed = new Multipart("mixed");
            if (body is not null)
                mixed.Add(body);
            foreach (var attachment in attachmentParts)
            {
                var built = await BuildPartAsync(accountId, context, attachment, bodyValues, cancellationToken);
                if (built.Error is not null) return built;
                mixed.Add(built.Entity!);
            }
            body = mixed;
        }
        return new PartBuildResult(body, null);
    }

    private async Task<PartBuildResult> BuildPartAsync(
        Guid accountId,
        JmapInvocationContext context,
        JsonObject value,
        JsonObject? bodyValues,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? forbiddenHeaders = null)
    {
        var invalid = value.Select(item => item.Key)
            .Where(property => !BodyProperties.Contains(property)
                && !property.StartsWith("header:", StringComparison.Ordinal))
            .ToArray();
        if (invalid.Length > 0 || value.ContainsKey("headers"))
            return PartBuildResult.Failed("invalidProperties", properties: invalid);
        if (!JmapMethodHelpers.TryGetRequiredString(value, "type", out var type)
            || !TryMimeType(type, out var mediaType, out var mediaSubtype)
            || !JmapMethodHelpers.TryGetOptionalString(value, "partId", out var partId)
            || !JmapMethodHelpers.TryGetOptionalString(value, "blobId", out var blobId)
            || !JmapMethodHelpers.TryGetOptionalString(value, "charset", out var charset)
            || !JmapMethodHelpers.TryGetOptionalString(value, "name", out var name)
            || !JmapMethodHelpers.TryGetOptionalString(value, "disposition", out var disposition)
            || !JmapMethodHelpers.TryGetOptionalString(value, "cid", out var contentId)
            || !JmapMethodHelpers.TryGetOptionalString(value, "location", out var location)
            || partId is not null && blobId is not null
            || !TryValidateOptionalSize(value))
        {
            return PartBuildResult.Failed("invalidProperties");
        }
        if (mediaType != "text" && charset is not null)
            return PartBuildResult.Failed("invalidProperties", properties: ["charset"]);

        if (mediaType == "multipart")
        {
            if (partId is not null || blobId is not null || value["subParts"] is not JsonArray subParts)
                return PartBuildResult.Failed("invalidProperties");
            var multipart = new Multipart(mediaSubtype);
            foreach (var item in subParts)
            {
                if (item is not JsonObject child)
                    return PartBuildResult.Failed("invalidProperties");
                var built = await BuildPartAsync(accountId, context, child, bodyValues, cancellationToken);
                if (built.Error is not null)
                    return built;
                multipart.Add(built.Entity!);
            }
            if (!TryApplyPartMetadata(
                    multipart,
                    value,
                    name,
                    disposition,
                    contentId,
                    location,
                    forbiddenHeaders))
            {
                multipart.Dispose();
                return PartBuildResult.Failed("invalidProperties");
            }
            return new PartBuildResult(multipart, null);
        }
        if (value.TryGetPropertyValue("subParts", out var subPartsNode) && subPartsNode is not null)
            return PartBuildResult.Failed("invalidProperties", properties: ["subParts"]);

        MimePart part;
        if (partId is not null)
        {
            if (charset is not null
                || value.ContainsKey("size")
                || !TryGetBodyValue(bodyValues, partId, out var textValue)
                || mediaType != "text")
            {
                return PartBuildResult.Failed("invalidProperties");
            }
            part = new TextPart(mediaSubtype)
            {
                Text = textValue,
                ContentTransferEncoding = ContentEncoding.QuotedPrintable,
            };
        }
        else if (blobId is not null)
        {
            var resolvedBlobId = context.ResolveId(blobId);
            if (resolvedBlobId is null)
                return PartBuildResult.Failed("blobNotFound");
            var blob = await blobs.GetAsync(accountId, resolvedBlobId, cancellationToken);
            if (blob is null)
                return PartBuildResult.Failed("blobNotFound");
            part = new MimePart(mediaType, mediaSubtype)
            {
                Content = new MimeContent(new MemoryStream(blob.Content, writable: false), ContentEncoding.Default),
                ContentTransferEncoding = mediaType == "text"
                    ? ContentEncoding.QuotedPrintable
                    : ContentEncoding.Base64,
            };
        }
        else
        {
            return PartBuildResult.Failed("invalidProperties");
        }

        if (mediaType == "text" && charset is not null)
        {
            try
            {
                _ = Encoding.GetEncoding(charset);
                part.ContentType.Charset = charset;
            }
            catch (ArgumentException)
            {
                part.Dispose();
                return PartBuildResult.Failed("invalidProperties", properties: ["charset"]);
            }
        }
        if (!TryApplyPartMetadata(
                part,
                value,
                name,
                disposition,
                contentId,
                location,
                forbiddenHeaders))
        {
            part.Dispose();
            return PartBuildResult.Failed("invalidProperties");
        }
        return new PartBuildResult(part, null);
    }

    private static bool TryApplyHeaders(
        MimeMessage message,
        JsonObject value,
        out IReadOnlySet<string> representedHeaders,
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
            case "from": return TrySetAddressList(message.From, node);
            case "to": return TrySetAddressList(message.To, node);
            case "cc": return TrySetAddressList(message.Cc, node);
            case "bcc": return TrySetAddressList(message.Bcc, node);
            case "replyTo": return TrySetAddressList(message.ReplyTo, node);
            case "sender":
                if (node is null)
                    return true;
                if (!TryParseAddressList(node, out var senders) || senders.Count != 1)
                    return false;
                message.Sender = senders.Mailboxes.Single();
                return true;
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
                    || !JmapDate.TryParseDate(dateText, out var date))
                    return false;
                message.Date = date;
                return true;
            case "messageId":
                if (node is null)
                    return true;
                return TrySetMessageIds(node, ids =>
                {
                    if (ids.Count != 1) return false;
                    message.MessageId = ids[0];
                    return true;
                });
            case "inReplyTo":
                return TrySetMessageIds(node, ids =>
                {
                    if (ids.Count > 0)
                    {
                        message.Headers.Add(
                            HeaderId.InReplyTo,
                            string.Join(' ', ids.Select(id => $"<{id}>")));
                    }
                    return true;
                });
            case "references":
                return TrySetMessageIds(node, ids =>
                {
                    foreach (var id in ids) message.References.Add(id);
                    return true;
                });
            default:
                return false;
        }
    }

    private static bool TrySetAddressList(InternetAddressList target, JsonNode? node)
    {
        if (!TryParseAddressList(node, out var addresses))
            return false;
        target.AddRange(addresses);
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
                || !JmapMethodHelpers.TryGetRequiredString(address, "email", out var email)
                || !JmapMethodHelpers.TryGetOptionalString(address, "name", out var name)
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
                || !JmapMessageId.TryParseParsedForm(id, out var parsed))
            {
                return false;
            }
            ids.Add(parsed);
        }
        return setter(ids);
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
        parts = result;
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
        if (!JmapMediaType.TryNormalize(value, out var normalized))
            return false;
        var separator = normalized.IndexOf('/');
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
            || !JmapMethodHelpers.TryGetRequiredString(bodyValue, "value", out text)
            || !JmapMethodHelpers.TryGetOptionalBoolean(bodyValue, "isEncodingProblem", false, out var encodingProblem)
            || !JmapMethodHelpers.TryGetOptionalBoolean(bodyValue, "isTruncated", false, out var truncated)
            || bodyValue.Any(item => item.Key is not ("value" or "isEncodingProblem" or "isTruncated"))
            || encodingProblem
            || truncated)
        {
            return false;
        }
        return true;
    }

    private static bool TryApplyPartMetadata(
        MimeEntity entity,
        JsonObject source,
        string? name,
        string? disposition,
        string? contentId,
        string? location,
        IReadOnlySet<string>? forbiddenHeaders = null)
    {
        var representedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type",
        };
        if (name is not null || disposition is not null)
            representedHeaders.Add("Content-Disposition");
        if (contentId is not null)
            representedHeaders.Add("Content-ID");
        if (source.ContainsKey("language"))
            representedHeaders.Add("Content-Language");
        if (location is not null)
            representedHeaders.Add("Content-Location");

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
        try
        {
            if (disposition is not null)
            {
                if (!IsMimeToken(disposition.ToLowerInvariant()))
                    return false;
                entity.ContentDisposition = new ContentDisposition(disposition.ToLowerInvariant());
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
                        || !JmapLanguageTag.IsValid(language))
                    {
                        return false;
                    }
                    values.Add(language);
                }
                if (values.Count > 0)
                    entity.Headers["Content-Language"] = string.Join(", ", values);
            }
        }

        foreach (var item in source.Where(item => item.Key.StartsWith("header:", StringComparison.Ordinal)))
        {
            if (!TryParseWritableHeaderProperty(item.Key, out var header)
                || header.Name.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || representedHeaders.Contains(header.Name)
                || forbiddenHeaders?.Contains(header.Name) == true
                || !representedHeaders.Add(header.Name)
                || !TryAddHeaderValues(entity.Headers, header, item.Value))
            {
                return false;
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
                || !JmapMethodHelpers.TryGetRequiredString(bodyValue, "value", out _)
                || !JmapMethodHelpers.TryGetOptionalBoolean(
                    bodyValue,
                    "isEncodingProblem",
                    false,
                    out var encodingProblem)
                || !JmapMethodHelpers.TryGetOptionalBoolean(
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
                    if (!TryFormatHeaderValue(header.Form, item, out var value))
                        return false;
                    headers.Add(header.Name, value);
                }
                return true;
            }
            if (node is null)
                return true;
            if (!TryFormatHeaderValue(header.Form, node, out var singleValue))
                return false;
            headers.Add(header.Name, singleValue);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool IsAllowedHeaderForm(string name, string form)
    {
        if (form == "Raw")
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

    private static readonly IReadOnlySet<string> KnownHeaderNames = new HashSet<string>(
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
        if (form is "Raw" or "Text")
        {
            return node is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out value!);
        }
        if (form == "Addresses")
        {
            if (!TryParseAddressList(node, out var addresses)) return false;
            value = addresses.ToString();
            return true;
        }
        if (form == "GroupedAddresses")
        {
            if (node is not JsonArray groups) return false;
            var addresses = new InternetAddressList();
            foreach (var item in groups)
            {
                if (item is not JsonObject group
                    || !JmapMethodHelpers.TryGetOptionalString(group, "name", out var name)
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
        if (form == "MessageIds")
        {
            if (node is not JsonArray ids) return false;
            var parsed = new List<string>();
            foreach (var item in ids)
            {
                if (item is not JsonValue idValue
                    || !idValue.TryGetValue<string>(out var id)
                    || !JmapMessageId.TryParseParsedForm(id, out var parsedId)) return false;
                parsed.Add($"<{parsedId}>");
            }
            value = string.Join(' ', parsed);
            return true;
        }
        if (form == "Date")
        {
            if (node is not JsonValue dateValue
                || !dateValue.TryGetValue<string>(out var text)
                || !JmapDate.TryParseDate(text, out var date)) return false;
            value = date.ToString("r", CultureInfo.InvariantCulture);
            return true;
        }
        if (form == "URLs")
        {
            if (node is not JsonArray urls) return false;
            var parsed = new List<string>();
            foreach (var item in urls)
            {
                if (item is not JsonValue urlValue
                    || !urlValue.TryGetValue<string>(out var url)
                    || !JmapHeaderUrl.IsValidParsedForm(url)) return false;
                parsed.Add($"<{url}>");
            }
            value = string.Join(", ", parsed);
            return true;
        }
        return false;
    }

    private readonly record struct WritableHeader(string Name, string Form, bool All);

    private sealed record PartBuildResult(MimeEntity? Entity, JsonObject? Error)
    {
        public static PartBuildResult Failed(
            string type,
            string? description = null,
            IEnumerable<string>? properties = null) =>
            new(null, JmapMethodHelpers.SetError(type, description, properties));
    }
}
