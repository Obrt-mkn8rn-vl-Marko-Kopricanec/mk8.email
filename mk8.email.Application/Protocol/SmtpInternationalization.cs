using System.Text;

namespace mk8.email.Application.Protocol;

internal static class SmtpInternationalization
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryDecodeCommandLine(string wireValue, out string value)
    {
        value = string.Empty;
        if (wireValue.Any(character => character > byte.MaxValue))
            return false;

        try
        {
            value = StrictUtf8.GetString(MailWireEncoding.Instance.GetBytes(wireValue));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static bool HeadersRequireSmtpUtf8(string rawMessage) =>
        GetHeaderSection(rawMessage).Any(character => character > sbyte.MaxValue);

    public static bool HasValidUtf8Headers(string rawMessage)
    {
        var headers = GetHeaderSection(rawMessage);
        if (!headers.Any(character => character > sbyte.MaxValue))
            return true;
        if (headers.Any(character => character > byte.MaxValue))
            return false;

        try
        {
            _ = StrictUtf8.GetString(MailWireEncoding.Instance.GetBytes(headers));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static bool ContainsEightBit(string rawMessage) =>
        rawMessage.Any(character => character > sbyte.MaxValue);

    private static string GetHeaderSection(string rawMessage)
    {
        var separator = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator >= 0)
            return rawMessage[..separator];

        separator = rawMessage.IndexOf("\n\n", StringComparison.Ordinal);
        return separator >= 0 ? rawMessage[..separator] : rawMessage;
    }
}
