using System.Globalization;
using System.Text.RegularExpressions;

namespace mk8.email.Jmap;

internal static partial class JmapDate
{
    public static bool TryParseDate(string? value, out DateTimeOffset result) =>
        TryParse(value, requireUtc: false, out result);

    public static bool TryParseUtcDate(string? value, out DateTimeOffset result) =>
        TryParse(value, requireUtc: true, out result);

    public static string FormatUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return FormatUtcTicks(utc.Ticks);
    }

    public static string FormatUtc(DateTimeOffset value) =>
        FormatUtcTicks(value.UtcTicks);

    public static string FormatDate(DateTimeOffset value)
    {
        var baseValue = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = FormatFraction(value.Ticks);
        var offset = value.Offset == TimeSpan.Zero
            ? "Z"
            : value.ToString("zzz", CultureInfo.InvariantCulture);
        return baseValue + fraction + offset;
    }

    private static bool TryParse(string? value, bool requireUtc, out DateTimeOffset result)
    {
        result = default;
        if (value is null || !DatePattern().IsMatch(value))
            return false;
        if (requireUtc && value[^1] != 'Z')
            return false;

        var fractionSeparator = value.IndexOf('.', StringComparison.Ordinal);
        if (fractionSeparator >= 0)
        {
            var zoneIndex = value.IndexOfAny(['Z', '+', '-'], fractionSeparator + 1);
            if (zoneIndex < 0)
                return false;
            var fraction = value.AsSpan(fractionSeparator + 1, zoneIndex - fractionSeparator - 1);
            if (fraction.IndexOfAnyExcept('0') < 0)
                return false;
            if (fraction.Length > 7)
                value = value[..(fractionSeparator + 8)] + value[zoneIndex..];
        }

        var formats = fractionSeparator < 0
            ? DateFormatsWithoutFraction
            : DateFormatsWithFraction;
        return DateTimeOffset.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            value[^1] == 'Z'
                ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
                : DateTimeStyles.None,
            out result);
    }

    private static string FormatUtcTicks(long ticks)
    {
        var value = new DateTime(ticks, DateTimeKind.Utc);
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + FormatFraction(ticks)
            + "Z";
    }

    private static string FormatFraction(long ticks)
    {
        var remainder = ticks % TimeSpan.TicksPerSecond;
        return remainder == 0
            ? string.Empty
            : "." + remainder.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    private static readonly string[] DateFormatsWithoutFraction =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
    ];

    private static readonly string[] DateFormatsWithFraction =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    [GeneratedRegex(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
