using System.Globalization;
using System.Text.RegularExpressions;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static partial class GatewayDraftDateCodec
{
    public static bool TryParseDate(string? value, out DateTimeOffset result) =>
        TryParse(value, requireUtc: false, out result);

    private static bool TryParse(string? value, bool requireUtc, out DateTimeOffset result)
    {
        result = default;
        if (value is null || !DatePattern().IsMatch(value))
            return false;
        if (requireUtc && value[^1] != 'Z')
            return false;

        var isLeapSecond = value.AsSpan(17, 2).SequenceEqual("60");
        if (isLeapSecond)
            value = value[..17] + "59" + value[19..];

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
        if (!DateTimeOffset.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                value[^1] == 'Z'
                    ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
                    : DateTimeStyles.None,
                out result)
            && !TryParseExtendedOffset(value, fractionSeparator >= 0, out result))
        {
            return false;
        }
        if (!isLeapSecond)
            return true;

        var utc = result.UtcDateTime;
        if (utc.Hour != 23
            || utc.Minute != 59
            || utc.Second != 59
            || utc.Day != DateTime.DaysInMonth(utc.Year, utc.Month)
            || utc.Month is not (6 or 12)
            || result > DateTimeOffset.MaxValue.AddSeconds(-1))
        {
            result = default;
            return false;
        }
        result = result.AddSeconds(1);
        return true;
    }

    private static bool TryParseExtendedOffset(
        string value,
        bool hasFraction,
        out DateTimeOffset result)
    {
        result = default;
        if (value[^1] == 'Z')
            return false;

        var offsetIndex = value.Length - 6;
        if (offsetIndex <= 0
            || value[offsetIndex] is not ('+' or '-')
            || !int.TryParse(
                value.AsSpan(offsetIndex + 1, 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offsetHours)
            || !int.TryParse(
                value.AsSpan(offsetIndex + 4, 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offsetMinutes)
            || offsetHours > 23
            || offsetMinutes > 59)
        {
            return false;
        }

        var format = hasFraction
            ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
            : "yyyy-MM-dd'T'HH:mm:ss";
        if (!DateTime.TryParseExact(
                value.AsSpan(0, offsetIndex),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return false;
        }

        var offsetTicks = (offsetHours * TimeSpan.TicksPerHour)
            + (offsetMinutes * TimeSpan.TicksPerMinute);
        if (value[offsetIndex] == '-')
            offsetTicks = -offsetTicks;
        var utcTicks = local.Ticks - offsetTicks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            return false;

        result = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        return true;
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
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DatePattern();
}
