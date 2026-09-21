using System.Globalization;
using System.Text;

namespace mk8.email.Application.Protocol;

internal static class SmtpDsn
{
    private const string AtomSpecials = "!#$%&'*+-/=?^_`{|}~";

    public static bool TryNormalizeReturnContent(string value, out string normalized)
    {
        normalized = value.ToUpperInvariant();
        return normalized is "FULL" or "HDRS";
    }

    public static bool TryValidateEnvelopeId(string value)
    {
        return value.Length is > 0 and <= 100
            && TryDecodeXtext(value, out var decoded)
            && decoded.All(character => character is '\t' or >= ' ' and <= '~');
    }

    public static bool TryNormalizeNotify(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length is 0 or > 28)
            return false;

        if (value.Equals("NEVER", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "NEVER";
            return true;
        }

        var values = value.Split(',', StringSplitOptions.None);
        if (values.Length is 0 or > 3)
            return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedValues = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var item = values[index].ToUpperInvariant();
            if (item is not ("SUCCESS" or "FAILURE" or "DELAY")
                || !seen.Add(item))
                return false;
            normalizedValues[index] = item;
        }

        normalized = string.Join(',', normalizedValues);
        return true;
    }

    public static bool TryValidateOriginalRecipient(
        string value,
        bool smtpUtf8,
        out string addressType,
        out string decodedAddress)
    {
        addressType = string.Empty;
        decodedAddress = string.Empty;
        if (value.Length is 0 or > 500
            || Encoding.UTF8.GetByteCount(value) > 500)
        {
            return false;
        }

        var separator = value.IndexOf(';');
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        addressType = value[..separator];
        if (!IsAtom(addressType))
            return false;

        var encodedAddress = value[(separator + 1)..];
        if (addressType.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeUtf8Address(encodedAddress, smtpUtf8, out decodedAddress))
                return false;
            return SmtpAddress.TryNormalize(
                decodedAddress,
                allowEmpty: false,
                out _,
                out _);
        }

        return TryDecodeXtext(encodedAddress, out decodedAddress)
            && decodedAddress.Length > 0
            && decodedAddress.All(character => character is '\t' or >= ' ' and <= '~');
    }

    public static bool TryDecodeEnvelopeId(string? value, out string decoded)
    {
        if (value is null)
        {
            decoded = string.Empty;
            return false;
        }
        return TryDecodeXtext(value, out decoded);
    }

    public static bool Requests(string? notify, string condition)
    {
        return notify?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Contains(condition, StringComparer.OrdinalIgnoreCase) == true;
    }

    public static bool SuppressesAll(string? notify) =>
        string.Equals(notify, "NEVER", StringComparison.OrdinalIgnoreCase);

    private static bool TryDecodeXtext(string value, out string decoded)
    {
        decoded = string.Empty;
        var bytes = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '+')
            {
                if (index + 2 >= value.Length
                    || !TryUpperHex(value[index + 1], out var high)
                    || !TryUpperHex(value[index + 2], out var low))
                {
                    return false;
                }
                var decodedByte = (byte)((high << 4) | low);
                if (decodedByte > sbyte.MaxValue)
                    return false;
                bytes.Add(decodedByte);
                index += 2;
                continue;
            }

            if (character is < '!' or > '~' or '=')
                return false;
            bytes.Add((byte)character);
        }

        decoded = Encoding.ASCII.GetString(bytes.ToArray());
        return true;
    }

    private static bool TryDecodeUtf8Address(
        string value,
        bool smtpUtf8,
        out string decoded)
    {
        decoded = string.Empty;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                if (index + 4 >= value.Length
                    || value[index + 1] != 'x'
                    || value[index + 2] != '{')
                {
                    return false;
                }
                var close = value.IndexOf('}', index + 3);
                if (close < 0
                    || close - index - 3 is < 2 or > 6
                    || !int.TryParse(
                        value.AsSpan(index + 3, close - index - 3),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out var scalar)
                    || !Rune.IsValid(scalar))
                {
                    return false;
                }
                builder.Append(new Rune(scalar));
                index = close;
                continue;
            }

            if (char.IsAscii(character))
            {
                if (character is < '!' or > '~' or '+' or '=')
                    return false;
                builder.Append(character);
                continue;
            }

            if (!smtpUtf8)
                return false;
            builder.Append(character);
        }

        try
        {
            decoded = builder.ToString().Normalize(NormalizationForm.FormC);
            _ = new UTF8Encoding(false, true).GetByteCount(decoded);
            return decoded.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsAtom(string value) =>
        value.Length > 0
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || AtomSpecials.Contains(character, StringComparison.Ordinal));

    private static bool TryUpperHex(char value, out int digit)
    {
        if (value is >= '0' and <= '9')
        {
            digit = value - '0';
            return true;
        }
        if (value is >= 'A' and <= 'F')
        {
            digit = value - 'A' + 10;
            return true;
        }
        digit = 0;
        return false;
    }
}
