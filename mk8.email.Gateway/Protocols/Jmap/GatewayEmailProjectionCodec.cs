using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailProjectionCodec
{
    internal static readonly IReadOnlyList<string> GetDefaults =
    [
        "id", "blobId", "threadId", "mailboxIds", "keywords", "size",
        "receivedAt", "messageId", "inReplyTo", "references", "sender",
        "from", "to", "cc", "bcc", "replyTo", "subject", "sentAt",
        "hasAttachment", "preview", "bodyValues", "textBody", "htmlBody",
        "attachments",
    ];

    internal static readonly IReadOnlyList<string> ParseDefaults =
    [
        "messageId", "inReplyTo", "references", "sender", "from", "to",
        "cc", "bcc", "replyTo", "subject", "sentAt", "hasAttachment",
        "preview", "bodyValues", "textBody", "htmlBody", "attachments",
    ];

    internal static readonly IReadOnlyList<string> BodyDefaults =
    [
        "partId", "blobId", "size", "name", "type", "charset",
        "disposition", "cid", "language", "location",
    ];

    private static readonly HashSet<string> MessageProperties = new(
        GetDefaults.Concat(["headers", "bodyStructure"]), StringComparer.Ordinal);
    private static readonly HashSet<string> BodyProperties = new(
        BodyDefaults.Concat(["headers", "subParts"]), StringComparer.Ordinal);
    private static readonly HashSet<string> KnownHeaderNames = new(
    [
        "DATE", "FROM", "SENDER", "REPLY-TO", "TO", "CC", "BCC",
        "MESSAGE-ID", "IN-REPLY-TO", "REFERENCES", "SUBJECT", "COMMENTS",
        "KEYWORDS", "RESENT-DATE", "RESENT-FROM", "RESENT-SENDER",
        "RESENT-REPLY-TO", "RESENT-TO", "RESENT-CC", "RESENT-BCC", "RESENT-MESSAGE-ID",
        "RETURN-PATH", "RECEIVED", "LIST-HELP", "LIST-UNSUBSCRIBE", "LIST-SUBSCRIBE",
        "LIST-POST", "LIST-OWNER", "LIST-ARCHIVE",
    ], StringComparer.Ordinal);

    public static bool TryParse(JsonObject arguments, bool allowNullProperties,
        IReadOnlyList<string> defaults, out GatewayEmailProjectionOptions options)
    {
        options = null!;
        if (!TryStringArray(arguments, "properties", allowNullProperties, out var properties)
            || !TryStringArray(arguments, "bodyProperties", false, out var bodyProperties)
            || !TryBoolean(arguments, "fetchTextBodyValues", out var fetchText)
            || !TryBoolean(arguments, "fetchHTMLBodyValues", out var fetchHtml)
            || !TryBoolean(arguments, "fetchAllBodyValues", out var fetchAll)
            || !TryUnsigned(arguments, "maxBodyValueBytes", out var maximumBytes))
            return false;
        var selected = properties ?? defaults;
        var selectedBody = bodyProperties ?? BodyDefaults;
        if (selected.Any(property => !IsValidProperty(property, body: false))
            || selectedBody.Any(property => !IsValidProperty(property, body: true)))
            return false;
        options = new(selected, selectedBody, fetchText, fetchHtml, fetchAll,
            checked((int)Math.Min(maximumBytes ?? 0, int.MaxValue)));
        return true;
    }

    private static bool TryStringArray(JsonObject arguments, string name, bool nullable,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node)) return true;
        if (node is null) return nullable;
        if (node is not JsonArray array || array.Any(item => item is not JsonValue value
            || !value.TryGetValue<string>(out var text) || text is null)) return false;
        values = array.Select(item => item!.GetValue<string>()).ToArray();
        return true;
    }

    private static bool TryBoolean(JsonObject arguments, string name, out bool value)
    {
        value = false;
        return !arguments.TryGetPropertyValue(name, out var node)
            || node is JsonValue scalar && scalar.TryGetValue<bool>(out value);
    }

    private static bool TryUnsigned(JsonObject arguments, string name, out long? value)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(name, out var node)) return true;
        if (node is not JsonValue scalar || !TryInteger(scalar, out var parsed)
            || parsed is < 0 or > 9_007_199_254_740_991) return false;
        value = parsed;
        return true;
    }

    private static bool TryInteger(JsonValue value, out long result)
    {
        if (value.TryGetValue<long>(out result)) return true;
        if (value.TryGetValue<int>(out var signed)) { result = signed; return true; }
        if (value.TryGetValue<uint>(out var unsigned)) { result = unsigned; return true; }
        if (value.TryGetValue<ulong>(out var wide) && wide <= long.MaxValue)
        {
            result = checked((long)wide);
            return true;
        }
        result = 0;
        return false;
    }

    internal static bool IsValidProperty(string property, bool body) =>
        (body ? BodyProperties : MessageProperties).Contains(property)
        || TryParseHeaderProperty(property);

    private static bool TryParseHeaderProperty(string property)
    {
        if (!property.StartsWith("header:", StringComparison.Ordinal)) return false;
        var remainder = property[7..];
        if (remainder.EndsWith(":all", StringComparison.Ordinal)) remainder = remainder[..^4];
        var form = HeaderForm.Raw;
        var asIndex = remainder.LastIndexOf(":as", StringComparison.Ordinal);
        if (asIndex >= 0)
        {
            var formName = remainder[(asIndex + 3)..];
            remainder = remainder[..asIndex];
            if (formName is not ("Raw" or "Text" or "Addresses" or "GroupedAddresses" or "MessageIds" or "Date" or "URLs")
                || !Enum.TryParse(formName, ignoreCase: false, out form)) return false;
        }
        return remainder.Length > 0
            && remainder.All(character => character is >= (char)33 and <= (char)126 && character != ':')
            && IsAllowedHeaderForm(remainder, form);
    }

    private static bool IsAllowedHeaderForm(string name, HeaderForm form)
    {
        if (form == HeaderForm.Raw) return true;
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

    private enum HeaderForm { Raw, Text, Addresses, GroupedAddresses, MessageIds, Date, URLs }
}
