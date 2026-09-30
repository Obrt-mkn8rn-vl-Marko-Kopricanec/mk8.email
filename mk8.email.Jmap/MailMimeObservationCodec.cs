using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static partial class MailMimeObservationCodec
{
    internal sealed class Observer
    {
        private readonly MailMessageSnapshot _snapshot;
        private readonly PartDescriptor? _parts;
        private readonly BodyPartSelection _selection;

        public Observer(MailMessageSnapshot snapshot)
        {
            ValidateSnapshot(snapshot);
            _snapshot = snapshot;
            _parts = snapshot.RootPart is { } root ? BuildPart(snapshot, root) : null;
            _selection = SelectBodyParts(_parts);
        }

        public JsonNode? Observe(MailMessageAssertion assertion,
            IReadOnlyList<MailMimeFieldSelection> fields, int summaryRunes)
        {
            var snapshot = _snapshot;
            var parts = _parts;
            var selection = _selection;
            var stored = snapshot.Stored ?? throw new InvalidOperationException("Stored MIME metadata is missing.");
            return assertion.Field switch
            {
                MailMessageObservationField.Id => JsonValue.Create(JmapId.Email(stored.Id)),
                MailMessageObservationField.ContentReference => JsonValue.Create(JmapId.RawBlob(stored.Id)),
                MailMessageObservationField.ThreadReference => JsonValue.Create(JmapId.Thread(stored.ThreadKey)),
                MailMessageObservationField.Length => JsonValue.Create(stored.Size),
                MailMessageObservationField.ArrivalTime => JsonValue.Create(FormatUtcDate(stored.ReceivedAt)),
                MailMessageObservationField.AttachmentPresence => JsonValue.Create(selection.Attachments.Any(
                    part => !string.Equals(part.Disposition, "inline", StringComparison.OrdinalIgnoreCase))),
                MailMessageObservationField.Summary => JsonValue.Create(BuildPreview(
                    selection.TextCandidates, selection.HtmlCandidates, summaryRunes)),
                MailMessageObservationField.RawHeaders => BuildHeaders(ReadHeaders(snapshot.Headers)),
                MailMessageObservationField.Header => ObserveHeader(snapshot, assertion.Header!),
                MailMessageObservationField.BodyTree => parts is null ? null : BuildPartJson(parts, fields, true),
                MailMessageObservationField.PlainParts => BuildPartList(selection.TextBody, fields),
                MailMessageObservationField.HtmlParts => BuildPartList(selection.HtmlBody, fields),
                MailMessageObservationField.Attachments => BuildPartList(selection.Attachments, fields),
                MailMessageObservationField.TextValues => BuildBodyValues(selection.LeafParts, selection.TextBody,
                    selection.HtmlBody, false, false, true),
                _ => throw new InvalidOperationException("The MIME observation field is invalid."),
            };
        }

        private static JsonNode? ObserveHeader(MailMessageSnapshot snapshot, MailHeaderObservation header) =>
            HeaderValue(ReadHeaders(snapshot.Headers), header.Name, header.Form, header.All);

        public JsonObject ObserveTextValues(bool text, bool html, bool all)
        {
            return BuildBodyValues(_selection.LeafParts, _selection.TextBody, _selection.HtmlBody, text, html, all);
        }


    }

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

    private static string BodyPartBlob(Guid sourceId, string path)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var id = $"R{sourceId:N}_{encoded}";
        if (JmapId.IsValidId(id)) return id;
        var digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"H{sourceId:N}_{path.Count(character => character == '!')}_{digest}";
    }

    private static string FormatUtcDate(DateTime value) => JmapDate.FormatUtc(value);

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
        IReadOnlyList<MailMimeFieldSelection> properties,
        bool includeSubParts = false)
    {
        var result = new JsonArray();
        foreach (var part in parts)
            result.Add(BuildPartJson(part, properties, includeSubParts));
        return result;
    }

    private static JsonObject BuildPartJson(PartDescriptor part,
        IReadOnlyList<MailMimeFieldSelection> properties, bool includeSubParts)
    {
        var result = new JsonObject();
        foreach (var property in properties)
            result[property.Key] = PartValue(part, property, properties, includeSubParts);
        if (includeSubParts && part.SubParts.Count > 0 && !result.ContainsKey(nameof(MailMimePartField.Children)))
            result[nameof(MailMimePartField.Children)] = BuildPartList(part.SubParts, properties, true);
        return result;
    }

    private static JsonNode? PartValue(PartDescriptor part, MailMimeFieldSelection field,
        IReadOnlyList<MailMimeFieldSelection> fields, bool includeSubParts) => field.Field switch
        {
            MailMimePartField.Path => JsonValue.Create(part.PartId),
            MailMimePartField.ContentReference => JsonValue.Create(part.BlobId),
            MailMimePartField.DecodedLength => JsonValue.Create(checked((int)part.Size)),
            MailMimePartField.RawHeaders => BuildHeaders(part.Headers),
            MailMimePartField.FileName => JsonValue.Create(part.Name),
            MailMimePartField.MediaType => JsonValue.Create(part.Type),
            MailMimePartField.Charset => JsonValue.Create(part.Charset),
            MailMimePartField.Disposition => JsonValue.Create(part.Disposition),
            MailMimePartField.ContentId => JsonValue.Create(part.ContentId),
            MailMimePartField.Languages => part.Language is null ? null :
                new JsonArray(part.Language.Select(language => (JsonNode?)JsonValue.Create(language)).ToArray()),
            MailMimePartField.Location => JsonValue.Create(part.Location),
            MailMimePartField.Children => part.SubParts.Count == 0 || !includeSubParts ? null
                : BuildPartList(part.SubParts, fields, true),
            MailMimePartField.Header => HeaderValue(part.Headers, field.Header!.Name, field.Header.Form, field.Header.All),
            _ => throw new InvalidOperationException("The MIME part field is invalid."),
        };

    private static JsonObject BuildBodyValues(
        IReadOnlyList<PartDescriptor> allParts,
        IReadOnlyList<PartDescriptor> textBody,
        IReadOnlyList<PartDescriptor> htmlBody,
        bool fetchText, bool fetchHtml, bool fetchAll)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (fetchAll)
        {
            foreach (var part in allParts.Where(part => part.Type.StartsWith("text/", StringComparison.Ordinal)))
                selected.Add(part.PartId!);
        }
        if (fetchText)
        {
            foreach (var part in textBody.Where(part => part.Type.StartsWith("text/", StringComparison.Ordinal)))
                selected.Add(part.PartId!);
        }
        if (fetchHtml)
        {
            foreach (var part in htmlBody.Where(part => part.Type.StartsWith("text/", StringComparison.Ordinal)))
                selected.Add(part.PartId!);
        }

        var result = new JsonObject();
        foreach (var part in allParts.Where(part => part.PartId is not null && selected.Contains(part.PartId)))
        {
            var (text, encodingProblem) = DecodeText(part);
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            result[part.PartId!] = new JsonObject
            {
                ["Text"] = text,
                ["EncodingError"] = encodingProblem,
                ["Incomplete"] = false,
            };
        }
        return result;
    }

    private static string BuildPreview(
        IReadOnlyList<PartDescriptor> textParts,
        IReadOnlyList<PartDescriptor> htmlParts, int maximumRunes)
    {
        var source = textParts.Count > 0 ? textParts[0]
            : htmlParts.Count > 0 ? htmlParts[0] : null;
        if (source is null)
            return string.Empty;
        var value = DecodeText(source).Text;
        if (string.Equals(source.Type, "text/html", StringComparison.Ordinal))
            value = JmapHtmlText.Extract(value);
        value = WhiteSpaceRegex().Replace(value, " ").Trim();
        return TruncateRunes(value, maximumRunes);
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
                ["FieldName"] = header.Field,
                ["RawText"] = RawHeaderValue(header),
            });
        }
        return result;
    }

    private static JsonNode? HeaderValue(
        IEnumerable<Header> headers,
        string name,
        MailHeaderValueForm form,
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

    private static JsonNode? ParseHeaderValue(Header header, MailHeaderValueForm form)
    {
        return form switch
        {
            MailHeaderValueForm.Raw => JsonValue.Create(RawHeaderValue(header)),
            MailHeaderValueForm.Text => JsonValue.Create(NormalizeDecodedHeaderText(header)),
            MailHeaderValueForm.Addresses => ParseAddresses(header, grouped: false),
            MailHeaderValueForm.GroupedAddresses => ParseAddresses(header, grouped: true),
            MailHeaderValueForm.MessageIds => ParseMessageIds(header.Value),
            MailHeaderValueForm.Date => DateUtils.TryParse(header.Value, out var date)
                ? JsonValue.Create(FormatDate(date))
                : null,
            MailHeaderValueForm.URLs => ParseUrls(header.Value),
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
                    ["DisplayName"] = string.IsNullOrEmpty(groupName) ? null : groupName,
                    ["Members"] = BuildAddressArray(group.Members.Mailboxes, rawTabMarker),
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
                ["DisplayName"] = null,
                ["Members"] = BuildAddressArray(ungrouped, rawTabMarker),
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
            ["DisplayName"] = string.IsNullOrEmpty(name) ? null : name,
            ["Address"] = mailbox.Address,
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
            if (!JmapHeaderUrl.IsValidParsedForm(url))
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

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WhiteSpaceRegex();

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
