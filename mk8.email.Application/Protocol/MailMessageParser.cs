using System.Text;

namespace mk8.email.Application.Protocol;

internal readonly record struct ParsedMailMessage(
    string Subject,
    string Body,
    string Headers);

internal static class MailMessageParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ParsedMailMessage Parse(string rawMessage)
    {
        var separatorIndex = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separatorIndex < 0)
        {
            separatorIndex = rawMessage.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        var wireHeaders = separatorIndex >= 0
            ? rawMessage[..separatorIndex]
            : rawMessage;
        var wireBody = separatorIndex >= 0
            ? rawMessage[(separatorIndex + separatorLength)..]
            : string.Empty;
        var headers = DecodeUtf8WireValue(wireHeaders);
        var body = DecodeUtf8WireValue(wireBody);
        return new ParsedMailMessage(
            ExtractHeaderValue(headers, "Subject"),
            body,
            headers);
    }

    public static string ExtractHeaderValue(string headers, string fieldName)
    {
        var lines = headers.Split('\n');
        var value = new StringBuilder();
        var found = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            if (found && trimmed.Length > 0 && trimmed[0] is ' ' or '\t')
            {
                value.Append(' ').Append(trimmed.Trim());
                continue;
            }

            if (found)
                break;

            if (trimmed.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
            {
                value.Append(trimmed[(fieldName.Length + 1)..].Trim());
                found = true;
            }
        }

        return value.ToString();
    }

    private static string DecodeUtf8WireValue(string value)
    {
        if (!value.Any(character => character > sbyte.MaxValue)
            || value.Any(character => character > byte.MaxValue))
        {
            return value;
        }

        try
        {
            return StrictUtf8.GetString(MailWireEncoding.Instance.GetBytes(value));
        }
        catch (DecoderFallbackException)
        {
            return value;
        }
    }
}
