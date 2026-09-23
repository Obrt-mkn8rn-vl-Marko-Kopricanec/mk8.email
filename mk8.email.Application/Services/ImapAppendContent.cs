using System.Security.Cryptography;
using System.Text;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal static class ImapAppendContent
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryPrepareForParsing(
        string messageData,
        bool utf8Enabled,
        out string messageForParsing)
    {
        messageForParsing = messageData;
        if (utf8Enabled && TryDecodeUtf8WireValue(messageData, out var decodedMessage))
        {
            messageForParsing = decodedMessage;
            return true;
        }

        var separatorIndex = messageData.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separatorIndex < 0)
            separatorIndex = messageData.IndexOf("\n\n", StringComparison.Ordinal);

        var headerLength = separatorIndex >= 0 ? separatorIndex : messageData.Length;
        var wireHeaders = messageData[..headerLength];
        if (!wireHeaders.Any(character => character > 0x7f))
            return true;
        if (!utf8Enabled || !TryDecodeUtf8WireValue(wireHeaders, out var decodedHeaders))
            return false;

        messageForParsing = decodedHeaders + messageData[headerLength..];
        return true;
    }

    public static string GenerateThreadObjectId(string? inReplyTo, string? messageId)
    {
        var value = !string.IsNullOrEmpty(inReplyTo) ? inReplyTo : messageId;
        if (string.IsNullOrEmpty(value))
            return Guid.CreateVersion7().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash[..16]);
    }

    private static bool TryDecodeUtf8WireValue(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Any(character => character > byte.MaxValue))
            return false;
        try
        {
            decoded = StrictUtf8.GetString(MailWireEncoding.Instance.GetBytes(value));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
