using System.Globalization;
using System.Text;

namespace mk8.email.Application.Protocol;

internal static class SmtpAddress
{
    private const string AllowedAtomCharacters = "!#$%&'*+-/=?^_`{|}~";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryNormalize(string value, bool allowEmpty, out string address)
        => TryNormalize(value, allowEmpty, out address, out _);

    public static bool TryNormalize(
        string value,
        bool allowEmpty,
        out string address,
        out bool requiresSmtpUtf8)
    {
        address = string.Empty;
        requiresSmtpUtf8 = false;
        if (allowEmpty && value.Length == 0)
            return true;

        if (string.IsNullOrWhiteSpace(value)
            || value.ContainsAny(['\r', '\n', '\0']))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
            _ = StrictUtf8.GetByteCount(normalized);
        }
        catch (ArgumentException)
        {
            return false;
        }

        requiresSmtpUtf8 = normalized.Any(character => !char.IsAscii(character));
        if (StrictUtf8.GetByteCount(normalized) > 254)
            return false;
        if (!TryFindSeparator(normalized, out var separator)
            || separator <= 0
            || separator == normalized.Length - 1)
            return false;

        var localPart = normalized[..separator];
        if (!IsValidLocalPart(localPart))
            return false;

        string domain;
        try
        {
            domain = new IdnMapping()
                .GetAscii(normalized[(separator + 1)..])
                .ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (domain.Length is 0 or > 255
            || Uri.CheckHostName(domain) != UriHostNameType.Dns)
        {
            return false;
        }

        var normalizedLocalPart = localPart.ToLowerInvariant();
        if (StrictUtf8.GetByteCount(localPart) > 64
            || StrictUtf8.GetByteCount(normalizedLocalPart) > 64)
            return false;

        address = $"{normalizedLocalPart}@{domain}";
        return StrictUtf8.GetByteCount(address) <= 254;
    }

    private static bool IsLocalPartCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value)
        || value == '.'
        || AllowedAtomCharacters.Contains(value, StringComparison.Ordinal);

    private static bool TryFindSeparator(string value, out int separator)
    {
        separator = -1;
        if (value[0] != '"')
        {
            separator = value.LastIndexOf('@');
            return separator == value.IndexOf('@');
        }

        var escaped = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character != '"')
                continue;

            if (index + 1 >= value.Length || value[index + 1] != '@')
                return false;
            separator = index + 1;
            return value.IndexOf('@', separator + 1) < 0;
        }

        return false;
    }

    private static bool IsValidLocalPart(string value)
    {
        if (value[0] == '"')
            return IsValidQuotedLocalPart(value);

        return !value.StartsWith('.')
            && !value.EndsWith('.')
            && !value.Contains("..", StringComparison.Ordinal)
            && value.All(character =>
                !char.IsAscii(character) || IsLocalPartCharacter(character));
    }

    private static bool IsValidQuotedLocalPart(string value)
    {
        if (value.Length < 2 || value[^1] != '"')
            return false;

        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                if (++index >= value.Length - 1
                    || value[index] is < ' ' or > '~')
                {
                    return false;
                }
                continue;
            }

            if (character == '"'
                || (char.IsAscii(character) && character is < ' ' or > '~'))
            {
                return false;
            }
        }

        return true;
    }
}
