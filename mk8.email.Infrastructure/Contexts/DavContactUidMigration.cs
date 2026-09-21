using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace mk8.email.Infrastructure.Data;

internal static class DavContactUidMigration
{
    private const string EmbeddedProperty = "X-MK8-JSCONTACT";
    private const string HashProperty = "X-MK8-JSCONTACT-HASH";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Rewrite(byte[] content, string replacementUid)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                "A legacy duplicate contact has non-UTF-8 content and cannot be repaired without data loss.",
                exception);
        }

        var lines = Unfold(text).ToList();
        if (!lines.Any(line => HasPropertyName(line, "BEGIN")
                && PropertyValue(line).Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            || !lines.Any(line => HasPropertyName(line, "END")
                && PropertyValue(line).Equals("VCARD", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "A legacy duplicate contact is not a complete vCard and cannot be repaired safely.");
        }

        var uidIndexes = lines
            .Select((line, index) => (line, index))
            .Where(item => HasPropertyName(item.line, "UID"))
            .Select(item => item.index)
            .ToArray();
        if (uidIndexes.Length != 1)
        {
            throw new InvalidOperationException(
                "A legacy duplicate contact must contain exactly one UID before it can be repaired safely.");
        }
        lines[uidIndexes[0]] = "UID:" + replacementUid;

        var embeddedLines = lines.Where(line => HasPropertyName(line, EmbeddedProperty)).ToArray();
        if (embeddedLines.Length > 1)
        {
            throw new InvalidOperationException(
                "A legacy duplicate contact contains multiple embedded JSContact values and cannot be repaired safely.");
        }

        string? embedded = null;
        if (embeddedLines.Length == 1)
        {
            embedded = RewriteEmbedded(embeddedLines[0], replacementUid)
                ?? embeddedLines[0];
        }

        var core = lines
            .Where(line => !HasPropertyName(line, EmbeddedProperty)
                && !HasPropertyName(line, HashProperty))
            .ToList();
        var endIndex = core.FindLastIndex(line => HasPropertyName(line, "END")
            && PropertyValue(line).Equals("VCARD", StringComparison.OrdinalIgnoreCase));
        if (endIndex < 0)
            throw new InvalidOperationException("A legacy duplicate contact has no vCard terminator.");

        if (embedded is not null)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\r\n", core) + "\r\n")));
            core.Insert(endIndex++, HashProperty + ":" + hash);
            core.Insert(endIndex, embedded);
        }

        var output = new StringBuilder();
        foreach (var line in core)
        {
            foreach (var folded in Fold(line))
                output.Append(folded).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static string? RewriteEmbedded(string line, string replacementUid)
    {
        var encoded = PropertyValue(line);
        if (!TryBase64UrlDecode(encoded, out var bytes))
            return null;
        try
        {
            if (JsonNode.Parse(StrictUtf8.GetString(bytes)) is not JsonObject card)
                return null;
            card["uid"] = replacementUid;
            return EmbeddedProperty + ":" + Base64UrlEncode(
                Encoding.UTF8.GetBytes(card.ToJsonString()));
        }
        catch (Exception exception) when (exception is FormatException
            or System.Text.Json.JsonException
            or DecoderFallbackException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Unfold(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Split('\n'))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t' && result.Count > 0)
                result[^1] += line[1..];
            else if (line.Length > 0)
                result.Add(line);
        }
        return result;
    }

    private static IReadOnlyList<string> Fold(string line)
    {
        var result = new List<string>();
        var remaining = line.AsSpan();
        var first = true;
        while (!remaining.IsEmpty)
        {
            var maximumBytes = first ? 75 : 74;
            var characterCount = 0;
            var byteCount = 0;
            foreach (var rune in remaining.EnumerateRunes())
            {
                if (byteCount + rune.Utf8SequenceLength > maximumBytes)
                    break;
                byteCount += rune.Utf8SequenceLength;
                characterCount += rune.Utf16SequenceLength;
            }
            if (characterCount == 0)
                throw new InvalidOperationException("A vCard line contains an invalid UTF-16 sequence.");
            result.Add((first ? string.Empty : " ") + remaining[..characterCount].ToString());
            remaining = remaining[characterCount..];
            first = false;
        }
        return result;
    }

    private static bool HasPropertyName(string line, string expected)
    {
        var colon = FindUnescapedColon(line);
        if (colon <= 0)
            return false;
        var header = line[..colon];
        var parameter = header.IndexOf(';');
        if (parameter >= 0)
            header = header[..parameter];
        var group = header.LastIndexOf('.');
        if (group >= 0)
            header = header[(group + 1)..];
        return header.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string PropertyValue(string line)
    {
        var colon = FindUnescapedColon(line);
        return colon < 0 ? string.Empty : line[(colon + 1)..];
    }

    private static int FindUnescapedColon(string line)
    {
        var escaped = false;
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (!escaped && line[index] == '"')
                quoted = !quoted;
            else if (!escaped && !quoted && line[index] == ':')
                return index;
            escaped = !escaped && line[index] == '\\';
            if (line[index] != '\\')
                escaped = false;
        }
        return -1;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            return false;
        }
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => "!",
        };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
