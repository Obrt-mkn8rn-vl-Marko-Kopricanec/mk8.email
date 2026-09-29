using System.Globalization;
using System.Text.RegularExpressions;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static partial class GatewayJmapDateCodec
{
    public static string FormatUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var fractionTicks = utc.Ticks % TimeSpan.TicksPerSecond;
        var fraction = fractionTicks == 0
            ? string.Empty
            : "." + fractionTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + fraction + "Z";
    }

    public static bool TryParseUtc(string? value, out DateTime result)
    {
        result = default;
        if (value is null || !UtcDatePattern().IsMatch(value))
            return false;
        var leapSecond = value.AsSpan(17, 2).SequenceEqual("60");
        if (leapSecond)
            value = value[..17] + "59" + value[19..];
        var separator = value.IndexOf('.', StringComparison.Ordinal);
        if (separator >= 0)
        {
            var fraction = value.AsSpan(separator + 1, value.Length - separator - 2);
            if (fraction.IndexOfAnyExcept('0') < 0)
                return false;
            if (fraction.Length > 7)
                value = value[..(separator + 8)] + "Z";
        }
        var format = separator < 0
            ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
        if (!DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        if (leapSecond)
        {
            var utc = parsed.UtcDateTime;
            if (utc.Hour != 23 || utc.Minute != 59 || utc.Second != 59
                || utc.Day != DateTime.DaysInMonth(utc.Year, utc.Month)
                || utc.Month is not (6 or 12)
                || parsed > DateTimeOffset.MaxValue.AddSeconds(-1))
                return false;
            parsed = parsed.AddSeconds(1);
        }
        result = parsed.UtcDateTime;
        return true;
    }

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?Z$",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UtcDatePattern();
}
