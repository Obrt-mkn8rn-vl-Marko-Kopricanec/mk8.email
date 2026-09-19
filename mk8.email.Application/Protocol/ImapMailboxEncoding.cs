using System.Text;

namespace mk8.email.Application.Protocol;

internal static class ImapMailboxEncoding
{
    private static readonly Encoding StrictBigEndianUnicode = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    public static string Encode(string value)
    {
        var result = new StringBuilder(value.Length);
        var shifted = new StringBuilder();

        foreach (var character in value)
        {
            if (IsDirectCharacter(character))
            {
                FlushShiftedCharacters(result, shifted);
                result.Append(character);
            }
            else if (character == '&')
            {
                FlushShiftedCharacters(result, shifted);
                result.Append("&-");
            }
            else
            {
                shifted.Append(character);
            }
        }

        FlushShiftedCharacters(result, shifted);
        return result.ToString();
    }

    public static bool TryDecode(string value, out string decoded)
    {
        var result = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '&')
            {
                if (!IsDirectCharacter(character))
                {
                    decoded = string.Empty;
                    return false;
                }

                result.Append(character);
                continue;
            }

            var terminator = value.IndexOf('-', index + 1);
            if (terminator < 0)
            {
                decoded = string.Empty;
                return false;
            }

            if (terminator == index + 1)
            {
                result.Append('&');
                index = terminator;
                continue;
            }

            var modifiedBase64 = value[(index + 1)..terminator];
            if (!TryDecodeShiftedCharacters(modifiedBase64, out var shifted))
            {
                decoded = string.Empty;
                return false;
            }

            result.Append(shifted);
            index = terminator;
        }

        decoded = result.ToString();
        return true;
    }

    private static bool TryDecodeShiftedCharacters(string value, out string decoded)
    {
        try
        {
            var standardBase64 = value.Replace(',', '/');
            var remainder = standardBase64.Length % 4;
            if (remainder == 1)
            {
                decoded = string.Empty;
                return false;
            }

            if (remainder > 0)
                standardBase64 = standardBase64.PadRight(standardBase64.Length + 4 - remainder, '=');

            var bytes = Convert.FromBase64String(standardBase64);
            if (bytes.Length == 0 || bytes.Length % 2 != 0)
            {
                decoded = string.Empty;
                return false;
            }

            decoded = StrictBigEndianUnicode.GetString(bytes);
            if (decoded.Any(character => character == '&' || IsDirectCharacter(character)))
                return false;

            return string.Equals(Encode(decoded), $"&{value}-", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
        catch (DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static void FlushShiftedCharacters(StringBuilder result, StringBuilder shifted)
    {
        if (shifted.Length == 0)
            return;

        var bytes = StrictBigEndianUnicode.GetBytes(shifted.ToString());
        var encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('/', ',');
        result.Append('&').Append(encoded).Append('-');
        shifted.Clear();
    }

    private static bool IsDirectCharacter(char character) =>
        character is >= ' ' and <= '~' and not '&';
}
