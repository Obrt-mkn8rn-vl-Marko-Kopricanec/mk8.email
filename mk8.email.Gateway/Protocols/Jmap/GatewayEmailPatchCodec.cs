using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailPatchCodec
{
    private static readonly Dictionary<string, MailMimePartField> PartFields = new(StringComparer.Ordinal)
    {
        ["partId"] = MailMimePartField.Path,
        ["blobId"] = MailMimePartField.ContentReference,
        ["size"] = MailMimePartField.DecodedLength,
        ["headers"] = MailMimePartField.RawHeaders,
        ["name"] = MailMimePartField.FileName,
        ["type"] = MailMimePartField.MediaType,
        ["charset"] = MailMimePartField.Charset,
        ["disposition"] = MailMimePartField.Disposition,
        ["cid"] = MailMimePartField.ContentId,
        ["language"] = MailMimePartField.Languages,
        ["location"] = MailMimePartField.Location,
        ["subParts"] = MailMimePartField.Children,
    };

    private static readonly Dictionary<string, MailMessageObservationField> MessageFields = new(StringComparer.Ordinal)
    {
        ["id"] = MailMessageObservationField.Id,
        ["blobId"] = MailMessageObservationField.ContentReference,
        ["threadId"] = MailMessageObservationField.ThreadReference,
        ["size"] = MailMessageObservationField.Length,
        ["receivedAt"] = MailMessageObservationField.ArrivalTime,
        ["hasAttachment"] = MailMessageObservationField.AttachmentPresence,
        ["preview"] = MailMessageObservationField.Summary,
        ["headers"] = MailMessageObservationField.RawHeaders,
        ["bodyStructure"] = MailMessageObservationField.BodyTree,
        ["textBody"] = MailMessageObservationField.PlainParts,
        ["htmlBody"] = MailMessageObservationField.HtmlParts,
        ["attachments"] = MailMessageObservationField.Attachments,
        ["bodyValues"] = MailMessageObservationField.TextValues,
    };

    private static readonly Dictionary<string, MailHeaderObservation> MessageHeaders = new(StringComparer.Ordinal)
    {
        ["messageId"] = new("Message-ID", MailHeaderValueForm.MessageIds, false),
        ["inReplyTo"] = new("In-Reply-To", MailHeaderValueForm.MessageIds, false),
        ["references"] = new("References", MailHeaderValueForm.MessageIds, false),
        ["sender"] = new("Sender", MailHeaderValueForm.Addresses, false),
        ["from"] = new("From", MailHeaderValueForm.Addresses, false),
        ["to"] = new("To", MailHeaderValueForm.Addresses, false),
        ["cc"] = new("Cc", MailHeaderValueForm.Addresses, false),
        ["bcc"] = new("Bcc", MailHeaderValueForm.Addresses, false),
        ["replyTo"] = new("Reply-To", MailHeaderValueForm.Addresses, false),
        ["subject"] = new("Subject", MailHeaderValueForm.Text, false),
        ["sentAt"] = new("Date", MailHeaderValueForm.Date, false),
    };

    public static MailMessagePatch Parse(JsonObject patch)
    {
        var roots = patch.Select(item => Root(item.Key)).Distinct(StringComparer.Ordinal).ToArray();
        var needsMime = roots.Any(root => root is not ("mailboxIds" or "keywords"));
        var unknown = roots.Where(root => !GatewayEmailProjectionCodec.IsValidProperty(root, false))
            .Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) return Failed(needsMime, MailMessageMutationError.InvalidProperties, unknown);
        if (!TryPartSelections(patch, out var selections, out var keys, out var invalid))
            return Failed(needsMime, MailMessageMutationError.InvalidProperties, [invalid!]);
        if (!TryPaths(patch, out var paths)) return Failed(needsMime, MailMessageMutationError.InvalidPatch);
        var flags = new List<MailMessageFlagChange>();
        var assertions = new List<MailMessageAssertion>();
        foreach (var root in roots)
        {
            var entries = patch.Zip(paths).Where(item => string.Equals(item.Second[0], root, StringComparison.Ordinal)).ToArray();
            if (root is "mailboxIds" or "keywords")
            {
                if (entries.Any(item => item.Second.Length > 2))
                    return Failed(needsMime, MailMessageMutationError.InvalidPatch);
                flags.AddRange(entries.Select(item => FlagChange(root, item.Second, item.First.Value)));
            }
            else assertions.Add(Assertion(root, entries, keys));
        }
        return new(flags.ToArray(), assertions.ToArray(), selections, 256, needsMime, null);
    }

    public static IReadOnlyList<MailMessagePatchFragment> ParseFragments(JsonObject patch) =>
        patch.Select(item => Fragment(item.Key, item.Value)).ToArray();

    private static MailMessagePatchFragment Fragment(string key, JsonNode? value)
    {
        var parsed = Parse(new JsonObject { [key] = value?.DeepClone() });
        TryPath(key, out var path);
        var stage = parsed.Failure is null ? MailMessageFragmentStage.Ok
            : !GatewayEmailProjectionCodec.IsValidProperty(Root(key), false) ? MailMessageFragmentStage.UnknownObservation
            : parsed.Failure.Error == MailMessageMutationError.InvalidProperties ? MailMessageFragmentStage.UnsupportedView
            : MailMessageFragmentStage.InvalidPath;
        return new(key, path, stage, parsed);
    }

    private static MailMessageAssertion Assertion(string root,
        (KeyValuePair<string, JsonNode?> First, string[] Second)[] entries, Dictionary<string, string> keys)
    {
        var field = MessageFields.GetValueOrDefault(root, MailMessageObservationField.Header);
        var header = MessageHeaders.GetValueOrDefault(root);
        if (field == MailMessageObservationField.Header && header is null)
            GatewayEmailValueCodec.TryHeaderObservation(root, out header);
        var changes = entries.Select(item => new MailMessageValueChange(
            TranslatePath(item.Second[1..], field, header, keys), ApplicationValueCodec.Encode(
                TranslateValue(item.First.Value, item.Second[1..], field, header, keys)))).ToArray();
        return new(root, field, header, changes, field == MailMessageObservationField.TextValues
            && entries.Any(item => item.Second.Length == 1));
    }

    private static MailMessageFlagChange FlagChange(string root, string[] path, JsonNode? value)
    {
        var field = string.Equals(root, "mailboxIds", StringComparison.Ordinal) ? MailMessageFlagField.Folders : MailMessageFlagField.Keywords;
        if (path.Length == 1)
        {
            if (value is null) return new(field, MailMessageFlagChangeKind.Clear, [], false);
            return value is JsonObject map
                ? new(field, MailMessageFlagChangeKind.Replace, map.Select(item => FlagEntry(item.Key, item.Value)).ToArray(), false)
                : new(field, MailMessageFlagChangeKind.Replace, [], true);
        }
        return value is null ? new(field, MailMessageFlagChangeKind.Remove,
            [new(path[1], MailMessageFlagValue.Enabled)], false)
            : new(field, MailMessageFlagChangeKind.Set, [FlagEntry(path[1], value)], false);
    }

    private static MailMessageFlagEntry FlagEntry(string key, JsonNode? value) => new(key,
        value is JsonValue scalar && scalar.TryGetValue<bool>(out var enabled)
            ? enabled ? MailMessageFlagValue.Enabled : MailMessageFlagValue.Disabled : MailMessageFlagValue.Malformed);

    private static bool TryPartSelections(JsonObject patch, out IReadOnlyList<MailMimeFieldSelection> selections,
        out Dictionary<string, string> keys, out string? invalid)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var nullChildren = false;
        foreach (var name in new[] { "bodyStructure", "textBody", "htmlBody", "attachments" })
            if (patch.TryGetPropertyValue(name, out var value)) Collect(value, properties, ref nullChildren);
        foreach (var item in patch)
        {
            var separator = item.Key.IndexOf('/', StringComparison.Ordinal);
            if (separator < 0 || item.Key[..separator] is not ("bodyStructure" or "textBody" or "htmlBody" or "attachments")) continue;
            var remainder = item.Key[(separator + 1)..];
            var next = remainder.IndexOf('/', StringComparison.Ordinal);
            properties.Add(Decode(next < 0 ? remainder : remainder[..next]));
        }
        if (!nullChildren) properties.Remove("subParts");
        invalid = properties.FirstOrDefault(name => !GatewayEmailProjectionCodec.IsValidProperty(name, true));
        keys = new(StringComparer.Ordinal);
        var fields = new List<MailMimeFieldSelection>();
        if (invalid is null)
        {
            foreach (var property in properties)
            {
                if (PartFields.TryGetValue(property, out var field))
                {
                    keys.Add(property, field.ToString());
                    fields.Add(new(field.ToString(), field, null));
                }
                else
                {
                    GatewayEmailValueCodec.TryHeaderObservation(property, out var header);
                    var key = "Header" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(property)));
                    keys.Add(property, key);
                    fields.Add(new(key, MailMimePartField.Header, header));
                }
            }
        }
        keys["subParts"] = nameof(MailMimePartField.Children);
        selections = fields.ToArray();
        return invalid is null;
    }

    private static void Collect(JsonNode? node, HashSet<string> fields, ref bool nullChildren)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Collect(child, fields, ref nullChildren);
        }
        else if (node is JsonObject part)
        {
            foreach (var item in part)
            {
                fields.Add(item.Key);
                if (!string.Equals(item.Key, "subParts", StringComparison.Ordinal)) continue;
                if (item.Value is null) nullChildren = true;
                else Collect(item.Value, fields, ref nullChildren);
            }
        }
    }

    private static string[] TranslatePath(string[] path, MailMessageObservationField field,
        MailHeaderObservation? header, Dictionary<string, string> keys)
    {
        var context = Context(field, header);
        var translated = new string[path.Length];
        for (var index = 0; index < path.Length; index++)
        {
            translated[index] = TranslateKey(path[index], context, keys);
            context = ChildContext(context, path[index]);
        }
        return translated;
    }

    private static JsonNode? TranslateValue(JsonNode? value, string[] path, MailMessageObservationField field,
        MailHeaderObservation? header, Dictionary<string, string> keys)
    {
        var context = Context(field, header);
        foreach (var token in path) context = ChildContext(context, token);
        return TranslateNode(value, context, keys);
    }

    private static JsonNode? TranslateNode(JsonNode? value, ValueContext context, Dictionary<string, string> keys)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(item => TranslateNode(item, context, keys)).ToArray());
        if (value is not JsonObject map) return value?.DeepClone();
        var result = new JsonObject();
        foreach (var item in map)
            result[TranslateKey(item.Key, context, keys)] = TranslateNode(item.Value, ChildContext(context, item.Key), keys);
        return result;
    }

    private static ValueContext Context(MailMessageObservationField field, MailHeaderObservation? header) => field switch
    {
        MailMessageObservationField.RawHeaders => ValueContext.RawHeader,
        MailMessageObservationField.BodyTree or MailMessageObservationField.PlainParts
            or MailMessageObservationField.HtmlParts or MailMessageObservationField.Attachments => ValueContext.Part,
        MailMessageObservationField.TextValues => ValueContext.TextMap,
        MailMessageObservationField.Header when header?.Form == MailHeaderValueForm.Addresses => ValueContext.Address,
        MailMessageObservationField.Header when header?.Form == MailHeaderValueForm.GroupedAddresses => ValueContext.Group,
        _ => ValueContext.Opaque,
    };

    private static ValueContext ChildContext(ValueContext context, string key) => context switch
    {
        ValueContext.Part when string.Equals(key, "subParts", StringComparison.Ordinal) => ValueContext.Part,
        ValueContext.Part when string.Equals(key, "headers", StringComparison.Ordinal) => ValueContext.RawHeader,
        ValueContext.Part when key.StartsWith("header:", StringComparison.Ordinal) => HeaderContext(key),
        ValueContext.Group when string.Equals(key, "addresses", StringComparison.Ordinal) => ValueContext.Address,
        ValueContext.TextMap => ValueContext.TextValue,
        _ => ValueContext.Opaque,
    };

    private static ValueContext HeaderContext(string key)
    {
        GatewayEmailValueCodec.TryHeaderObservation(key, out var header);
        return Context(MailMessageObservationField.Header, header);
    }

    private static string TranslateKey(string key, ValueContext context, Dictionary<string, string> fields) => context switch
    {
        ValueContext.Part => fields.GetValueOrDefault(key, "!" + key),
        ValueContext.RawHeader => key switch { "name" => "FieldName", "value" => "RawText", _ => "!" + key },
        ValueContext.Address => key switch { "name" => "DisplayName", "email" => "Address", _ => "!" + key },
        ValueContext.Group => key switch { "name" => "DisplayName", "addresses" => "Members", _ => "!" + key },
        ValueContext.TextValue => key switch
        { "value" => "Text", "isEncodingProblem" => "EncodingError", "isTruncated" => "Incomplete", _ => "!" + key },
        _ => key,
    };

    private static string Root(string key)
    {
        var separator = key.IndexOf('/', StringComparison.Ordinal);
        return Decode(separator < 0 ? key : key[..separator]);
    }

    internal static bool TryPaths(JsonObject patch, out string[][] paths)
    {
        var parsed = new List<string[]>();
        foreach (var item in patch)
        {
            if (!TryPath(item.Key, out var path) || parsed.Any(other => Prefix(other, path) || Prefix(path, other)))
            {
                paths = [];
                return false;
            }
            parsed.Add(path);
        }
        paths = parsed.ToArray();
        return true;
    }

    private static bool TryPath(string key, out string[] path)
    {
        path = [];
        if (key.Length == 0 || key[0] == '/' || key[^1] == '/') return false;
        var tokens = key.Split('/');
        foreach (var token in tokens)
        {
            if (token.Length == 0) return false;
            for (var index = 0; index < token.Length; index++)
                if (token[index] == '~' && (++index == token.Length || token[index] is not ('0' or '1'))) return false;
        }
        path = tokens.Select(Decode).ToArray();
        return true;
    }

    private static bool Prefix(string[] first, string[] second) =>
        first.Length < second.Length && first.SequenceEqual(second.Take(first.Length), StringComparer.Ordinal);

    private static string Decode(string token) => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    private static MailMessagePatch Failed(bool mime, MailMessageMutationError error, string[]? properties = null) =>
        new([], [], [], 256, mime, new(error, null, properties, null));

    private enum ValueContext { Opaque, Part, RawHeader, Address, Group, TextMap, TextValue }
}
