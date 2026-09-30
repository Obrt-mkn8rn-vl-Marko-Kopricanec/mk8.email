using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static partial class GatewayEmailValueCodec
{
    public static JsonObject BuildEmail(MailMessageSnapshot snapshot, GatewayEmailProjectionOptions options)
    {
        ValidateSnapshot(snapshot);
        var parts = snapshot.RootPart is { } root ? BuildPart(snapshot, root) : null;
        var selection = SelectBodyParts(parts);
        var headers = ReadHeaders(snapshot.Headers);
        var result = new JsonObject();
        foreach (var property in options.Properties)
        {
            var stored = StoredProperty(snapshot, property);
            var body = BodyProperty(parts, selection, options, property);
            if (stored.Handled) result[property] = stored.Value;
            else if (body.Handled) result[property] = body.Value;
            else if (string.Equals(property, "headers", StringComparison.Ordinal)) result[property] = BuildHeaders(headers);
            else if (StandardHeaders.TryGetValue(property, out var standard))
                result[property] = HeaderValue(headers, standard.Name, standard.Form, standard.All);
            else if (TryParseHeaderProperty(property, out var dynamicHeader))
                result[property] = HeaderValue(headers, dynamicHeader.Name, dynamicHeader.Form, dynamicHeader.All);
        }
        if (snapshot.Stored is { } message) result["id"] = $"E{message.Id:N}";
        return result;
    }

    private static (bool Handled, JsonNode? Value) StoredProperty(MailMessageSnapshot snapshot, string property)
    {
        var stored = snapshot.Stored;
        return property switch
        {
            "id" => (true, stored is null ? null : JsonValue.Create($"E{stored.Id:N}")),
            "blobId" => (true, JsonValue.Create(stored is null ? snapshot.UploadedContentId : $"B{stored.Id:N}")),
            "threadId" => (true, stored is null ? null : JsonValue.Create(GatewayThreadGetCodec.FormatThreadId(stored.ThreadKey))),
            "mailboxIds" => (true, stored is null ? null : new JsonObject { [$"M{stored.FolderId:N}"] = true }),
            "keywords" => (true, stored is null ? null : BuildKeywords(stored)),
            "size" => (true, JsonValue.Create(stored?.Size ?? snapshot.RawSize)),
            "receivedAt" => (true, stored is null ? null : JsonValue.Create(FormatUtcDate(stored.ReceivedAt))),
            _ => (false, null),
        };
    }

    private static (bool Handled, JsonNode? Value) BodyProperty(PartDescriptor? parts,
        BodyPartSelection selection, GatewayEmailProjectionOptions options, string property) => property switch
        {
            "bodyStructure" => (true, parts is null ? null : BuildPartJson(parts, options.BodyProperties, includeSubParts: true)),
            "bodyValues" => (true, BuildBodyValues(selection.LeafParts, selection.TextBody, selection.HtmlBody, options)),
            "textBody" => (true, BuildPartList(selection.TextBody, options.BodyProperties)),
            "htmlBody" => (true, BuildPartList(selection.HtmlBody, options.BodyProperties)),
            "attachments" => (true, BuildPartList(selection.Attachments, options.BodyProperties)),
            "hasAttachment" => (true, JsonValue.Create(selection.Attachments.Any(part =>
                !string.Equals(part.Disposition, "inline", StringComparison.OrdinalIgnoreCase)))),
            "preview" => (true, JsonValue.Create(BuildPreview(selection.TextCandidates, selection.HtmlCandidates))),
            _ => (false, null),
        };

    private static readonly Dictionary<string, HeaderProperty> StandardHeaders = new(StringComparer.Ordinal)
    {
        ["messageId"] = new("Message-ID", HeaderForm.MessageIds, false),
        ["inReplyTo"] = new("In-Reply-To", HeaderForm.MessageIds, false),
        ["references"] = new("References", HeaderForm.MessageIds, false),
        ["sender"] = new("Sender", HeaderForm.Addresses, false),
        ["from"] = new("From", HeaderForm.Addresses, false),
        ["to"] = new("To", HeaderForm.Addresses, false),
        ["cc"] = new("Cc", HeaderForm.Addresses, false),
        ["bcc"] = new("Bcc", HeaderForm.Addresses, false),
        ["replyTo"] = new("Reply-To", HeaderForm.Addresses, false),
        ["subject"] = new("Subject", HeaderForm.Text, false),
        ["sentAt"] = new("Date", HeaderForm.Date, false),
    };

    private static void ValidateSnapshot(MailMessageSnapshot snapshot)
    {
        if (snapshot is null || snapshot.ContentSourceId == Guid.Empty || snapshot.Headers is null || snapshot.Parts is null
            || snapshot.RootPart is { } root && (root < 0 || root >= snapshot.Parts.Count)
            || snapshot.RootPart is null && snapshot.Parts.Count != 0
            || snapshot.RawSize < 0 || snapshot.Stored is { } stored
                && (stored.Id == Guid.Empty || stored.FolderId == Guid.Empty || stored.ThreadKey is null
                    || stored.Keywords is null || stored.Size < 0 || stored.ReceivedAt.Kind != DateTimeKind.Utc))
            throw new InvalidOperationException("The Application returned an invalid MIME snapshot.");
    }

    private static PartDescriptor BuildPart(MailMessageSnapshot snapshot, int index)
    {
        var part = snapshot.Parts[index];
        if (part is null || part.Headers is null || part.Children is null || part.MediaType is null
            || part.DecodedSize is < 0 or > int.MaxValue || part.Children.Any(child => child < 0 || child >= index)
            || part.Path is null && part.Children.Count == 0
                && !part.MediaType.StartsWith("multipart/", StringComparison.Ordinal))
            throw new InvalidOperationException("The Application returned an invalid MIME part.");
        return new(ReadHeaders(part.Headers), part.Path, part.Path is null ? null
                : BodyPartBlob(snapshot.ContentSourceId, snapshot.PartPrefix is null ? part.Path : $"{snapshot.PartPrefix}!{part.Path}"),
            part.DecodedSize, part.Text, part.EncodingProblem, part.Name, part.MediaType,
            part.Charset, part.Disposition, part.ContentId, part.Languages?.ToArray(), part.Location,
            part.Children.Select(child => BuildPart(snapshot, child)).ToArray());
    }

    private static Header[] ReadHeaders(IReadOnlyList<MailMimeHeaderSnapshot> snapshots)
    {
        var headers = new Header[snapshots.Count];
        for (var index = 0; index < snapshots.Count; index++)
        {
            var value = snapshots[index];
            if (value is null || value.RawField.IsEmpty)
                throw new InvalidOperationException("The Application returned invalid MIME headers.");
            var bytes = new byte[value.RawField.Length + value.RawValue.Length + 1];
            value.RawField.Span.CopyTo(bytes);
            bytes[value.RawField.Length] = (byte)':';
            value.RawValue.Span.CopyTo(bytes.AsSpan(value.RawField.Length + 1));
            if (!Header.TryParse(bytes, out var header))
                throw new InvalidOperationException("The Application returned an unparseable MIME header.");
            headers[index] = header;
        }
        return headers;
    }

    private static JsonObject BuildKeywords(MailStoredMessageSnapshot stored)
    {
        var result = new JsonObject();
        foreach (var keyword in stored.Keywords) result[keyword] = true;
        return result;
    }

    private static string BodyPartBlob(Guid sourceId, string path)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var id = $"R{sourceId:N}_{encoded}";
        if (GatewayJmapBatchCodec.IsId(id)) return id;
        var digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"H{sourceId:N}_{path.Count(character => character == '!')}_{digest}";
    }

    private static string FormatUtcDate(DateTime value) => GatewayJmapDateCodec.FormatUtc(value);

    private static string FormatDate(DateTimeOffset value)
    {
        var fractionTicks = value.Ticks % TimeSpan.TicksPerSecond;
        var fraction = fractionTicks == 0 ? string.Empty
            : "." + fractionTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + fraction
            + (value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture));
    }

    private static (string Text, bool EncodingProblem) DecodeText(PartDescriptor part) =>
        (part.Text ?? string.Empty, part.EncodingProblem);

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
                case "size": result[property] = checked((int)part.Size); break;
                case "headers": result[property] = BuildHeaders(part.Headers); break;
                case "name": result[property] = part.Name; break;
                case "type": result[property] = part.Type; break;
                case "charset": result[property] = part.Charset; break;
                case "disposition": result[property] = part.Disposition; break;
                case "cid": result[property] = part.ContentId; break;
                case "language":
                    result[property] = part.Language is null
                        ? null
                        : new JsonArray(part.Language.Select(language => (JsonNode?)JsonValue.Create(language)).ToArray());
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
                            part.Headers,
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
        GatewayEmailProjectionOptions options)
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
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var truncated = TruncateUtf8(
                text,
                options.MaxBodyValueBytes,
                avoidOpenHtmlTag: string.Equals(part.Type, "text/html", StringComparison.Ordinal));
            result[part.PartId!] = new JsonObject
            {
                ["value"] = truncated.Value,
                ["isEncodingProblem"] = encodingProblem,
                ["isTruncated"] = truncated.IsTruncated,
            };
        }
        return result;
    }

    private static (string Value, bool IsTruncated) TruncateUtf8(
        string value,
        int maximumBytes,
        bool avoidOpenHtmlTag)
    {
        if (maximumBytes <= 0 || Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return (value, false);
        var usedBytes = 0;
        var usedCharacters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > maximumBytes)
                break;
            usedBytes += rune.Utf8SequenceLength;
            usedCharacters += rune.Utf16SequenceLength;
        }

        if (avoidOpenHtmlTag)
        {
            var openTagStart = FindOpenHtmlTagStart(value, usedCharacters);
            if (openTagStart >= 0)
                usedCharacters = openTagStart;
        }
        return (value[..usedCharacters], true);
    }

    private static int FindOpenHtmlTagStart(string value, int endExclusive)
    {
        var tagStart = -1;
        var quote = '\0';
        var comment = false;
        for (var index = 0; index < endExclusive; index++)
        {
            var character = value[index];
            if (tagStart < 0)
            {
                if (character != '<' || !LooksLikeHtmlTag(value, index))
                    continue;
                tagStart = index;
                comment = index + 3 < value.Length
                    && value[index + 1] == '!'
                    && value[index + 2] == '-'
                    && value[index + 3] == '-';
                continue;
            }

            if (comment)
            {
                if (character == '-'
                    && index + 2 < endExclusive
                    && value[index + 1] == '-'
                    && value[index + 2] == '>')
                {
                    tagStart = -1;
                    comment = false;
                    index += 2;
                }
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }
            if (character == '>')
                tagStart = -1;
        }
        return tagStart;
    }

    private static bool LooksLikeHtmlTag(string value, int index)
    {
        if (index + 1 >= value.Length)
            return false;
        var next = value[index + 1];
        return char.IsAsciiLetter(next) || next is '/' or '!' or '?';
    }

    private static string BuildPreview(
        IReadOnlyList<PartDescriptor> textParts,
        IReadOnlyList<PartDescriptor> htmlParts)
    {
        var source = textParts.Count > 0 ? textParts[0]
            : htmlParts.Count > 0 ? htmlParts[0] : null;
        if (source is null)
            return string.Empty;
        var value = DecodeText(source).Text;
        if (string.Equals(source.Type, "text/html", StringComparison.Ordinal))
            value = GatewayHtmlText.Extract(value);
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

    private static JsonArray BuildHeaders(IEnumerable<Header> headers)
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
        IEnumerable<Header> headers,
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
            HeaderForm.Text => JsonValue.Create(NormalizeDecodedHeaderText(header)),
            HeaderForm.Addresses => ParseAddresses(header, grouped: false),
            HeaderForm.GroupedAddresses => ParseAddresses(header, grouped: true),
            HeaderForm.MessageIds => ParseMessageIds(header.Value),
            HeaderForm.Date => DateUtils.TryParse(header.Value, out var date)
                ? JsonValue.Create(FormatDate(date))
                : null,
            HeaderForm.URLs => ParseUrls(header.Value),
            _ => null,
        };
    }

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

    private static string RawHeaderValue(Header header)
    {
        var value = Encoding.UTF8.GetString(header.RawValue)
            .TrimEnd('\r', '\n')
            .Replace("\0", string.Empty, StringComparison.Ordinal);
        return value;
    }

    private static JsonArray? ParseAddresses(Header header, bool grouped)
    {
        var (value, rawTabMarker) = ProtectRawHeaderTabs(header);
        value = NormalizeDecodedHeaderText(value);
        if (!InternetAddressList.TryParse(value, out var addresses))
            return null;
        if (!grouped)
        {
            var flattened = new JsonArray();
            foreach (var mailbox in addresses.Mailboxes)
                flattened.Add(BuildAddress(mailbox, rawTabMarker));
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
                var groupName = NormalizeAddressText(group.Name, rawTabMarker);
                groups.Add(new JsonObject
                {
                    ["name"] = string.IsNullOrEmpty(groupName) ? null : groupName,
                    ["addresses"] = BuildAddressArray(group.Members.Mailboxes, rawTabMarker),
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
                ["addresses"] = BuildAddressArray(ungrouped, rawTabMarker),
            });
            ungrouped.Clear();
        }
    }

    private static JsonArray BuildAddressArray(
        IEnumerable<MailboxAddress> mailboxes,
        string? rawTabMarker)
    {
        var result = new JsonArray();
        foreach (var mailbox in mailboxes)
            result.Add(BuildAddress(mailbox, rawTabMarker));
        return result;
    }

    private static JsonObject BuildAddress(MailboxAddress mailbox, string? rawTabMarker)
    {
        var name = NormalizeAddressText(mailbox.Name, rawTabMarker);
        return new JsonObject
        {
            ["name"] = string.IsNullOrEmpty(name) ? null : name,
            ["email"] = mailbox.Address,
        };
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

    private static JsonArray? ParseMessageIds(string value)
    {
        var result = new JsonArray();
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
        var parsed = ParseMessageIds(value) as JsonArray;
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
                    && GatewayMessageId.TryParseParsedForm(candidate, out parsed))
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

    private static JsonArray? ParseUrls(string value)
    {
        var result = new JsonArray();
        var index = 0;
        if (!TrySkipHeaderCfws(value, ref index))
            return null;

        while (index < value.Length)
        {
            if (value[index] != '<')
                return result.Count == 0 ? null : result;

            var close = value.IndexOf('>', ++index);
            if (close < 0)
                return null;
            var url = RemoveListHeaderWhitespace(value[index..close]);
            if (!GatewayHeaderUrl.IsValidParsedForm(url))
                return null;
            result.Add(url);

            index = close + 1;
            if (!TrySkipHeaderCfws(value, ref index) || index == value.Length)
                return result;
            if (value[index] != ',')
                return result;

            index++;
            if (!TrySkipHeaderCfws(value, ref index) || index == value.Length)
                return result;
        }
        return result.Count == 0 ? null : result;
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

    private static string RemoveListHeaderWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is not (' ' or '\t' or '\r' or '\n'))
                builder.Append(character);
        }
        return builder.ToString();
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

    private static readonly HashSet<string> KnownHeaderNames = new HashSet<string>(
        [
            "DATE", "FROM", "SENDER", "REPLY-TO", "TO", "CC", "BCC",
            "MESSAGE-ID", "IN-REPLY-TO", "REFERENCES", "SUBJECT", "COMMENTS",
            "KEYWORDS", "RESENT-DATE", "RESENT-FROM", "RESENT-SENDER",
            "RESENT-REPLY-TO", "RESENT-TO", "RESENT-CC", "RESENT-BCC", "RESENT-MESSAGE-ID",
            "RETURN-PATH", "RECEIVED", "LIST-HELP", "LIST-UNSUBSCRIBE", "LIST-SUBSCRIBE",
            "LIST-POST", "LIST-OWNER", "LIST-ARCHIVE",
        ],
        StringComparer.Ordinal);

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant, 1000)]
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
        Header[] Headers,
        string? PartId,
        string? BlobId,
        long Size,
        string? Text,
        bool EncodingProblem,
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
