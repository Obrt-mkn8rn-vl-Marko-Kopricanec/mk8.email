using System.Globalization;
using System.Text.RegularExpressions;

namespace mk8.email.Jmap;

internal static partial class JmapDate
{
    public static bool TryParseDate(string? value, out DateTimeOffset result) =>
        TryParse(value, requireUtc: false, out result);

    public static bool TryParseUtcDate(string? value, out DateTimeOffset result) =>
        TryParse(value, requireUtc: true, out result);

    public static bool IsValidRfc5322DateTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var index = 0;
        DayOfWeek? suppliedDayOfWeek = null;
        var dayOfWeekStart = index;
        if (!TrySkipFws(value, ref index, required: false))
            return false;
        if (TryReadDayName(value, ref index, out var dayOfWeek)
            && TryReadCharacter(value, ref index, ','))
        {
            suppliedDayOfWeek = dayOfWeek;
        }
        else
        {
            index = dayOfWeekStart;
        }

        if (!TrySkipFws(value, ref index, required: false)
            || !TryReadNumber(value, ref index, 1, 2, out var day)
            || !TrySkipFws(value, ref index, required: true)
            || !TryReadMonth(value, ref index, out var month)
            || !TrySkipFws(value, ref index, required: true)
            || !TryReadYear(value, ref index, out var calendarYear)
            || !TrySkipFws(value, ref index, required: true)
            || !TryReadNumber(value, ref index, 2, 2, out var hour)
            || !TryReadCharacter(value, ref index, ':')
            || !TryReadNumber(value, ref index, 2, 2, out var minute))
        {
            return false;
        }

        var second = 0;
        if (index < value.Length && value[index] == ':')
        {
            index++;
            if (!TryReadNumber(value, ref index, 2, 2, out second))
                return false;
        }
        if (!TrySkipFws(value, ref index, required: true)
            || index >= value.Length
            || value[index++] is not ('+' or '-')
            || !TryReadNumber(value, ref index, 4, 4, out var zone)
            || !JmapEmailCodec.IsHeaderCfwsOnly(value[index..])
            || day == 0
            || day > DateTime.DaysInMonth(calendarYear, month)
            || hour > 23
            || minute > 59
            || second > 60
            || zone % 100 > 59)
        {
            return false;
        }

        return suppliedDayOfWeek is null
            || new DateTime(calendarYear, month, day).DayOfWeek == suppliedDayOfWeek;
    }

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

    private static bool TrySkipFws(
        string value,
        ref int index,
        bool required)
    {
        var start = index;
        while (index < value.Length && value[index] is ' ' or '\t')
            index++;
        if (index < value.Length && value[index] is '\r' or '\n')
        {
            if (value[index] != '\r'
                || index + 2 >= value.Length
                || value[index + 1] != '\n'
                || value[index + 2] is not (' ' or '\t'))
            {
                return false;
            }
            index += 3;
            while (index < value.Length && value[index] is ' ' or '\t')
                index++;
            if (index < value.Length && value[index] is '\r' or '\n')
                return false;
        }
        return !required || index > start;
    }

    private static bool TryReadDayName(
        string value,
        ref int index,
        out DayOfWeek result)
    {
        result = default;
        if (index + 3 > value.Length)
            return false;
        var token = value.AsSpan(index, 3);
        if (token.Equals("Sun", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Sunday;
        else if (token.Equals("Mon", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Monday;
        else if (token.Equals("Tue", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Tuesday;
        else if (token.Equals("Wed", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Wednesday;
        else if (token.Equals("Thu", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Thursday;
        else if (token.Equals("Fri", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Friday;
        else if (token.Equals("Sat", StringComparison.OrdinalIgnoreCase))
            result = DayOfWeek.Saturday;
        else
            return false;
        index += 3;
        return true;
    }

    private static bool TryReadMonth(string value, ref int index, out int result)
    {
        result = 0;
        if (index + 3 > value.Length)
            return false;
        var token = value.AsSpan(index, 3);
        for (var month = 0; month < MonthNames.Length; month++)
        {
            if (!token.Equals(MonthNames[month], StringComparison.OrdinalIgnoreCase))
                continue;
            result = month + 1;
            index += 3;
            return true;
        }
        return false;
    }

    private static bool TryReadYear(string value, ref int index, out int calendarYear)
    {
        calendarYear = 0;
        var start = index;
        var cappedYear = 0;
        var modulo400 = 0;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            var digit = value[index++] - '0';
            if (cappedYear < 10_000)
                cappedYear = Math.Min(10_000, (cappedYear * 10) + digit);
            modulo400 = ((modulo400 * 10) + digit) % 400;
        }
        if (index - start < 4 || cappedYear < 1900)
            return false;
        calendarYear = 2000 + modulo400;
        return true;
    }

    private static bool TryReadNumber(
        string value,
        ref int index,
        int minimumDigits,
        int maximumDigits,
        out int result)
    {
        result = 0;
        var start = index;
        while (index < value.Length
            && index - start < maximumDigits
            && char.IsAsciiDigit(value[index]))
        {
            result = (result * 10) + value[index++] - '0';
        }
        return index - start >= minimumDigits
            && (index >= value.Length || !char.IsAsciiDigit(value[index]));
    }

    private static bool TryReadCharacter(string value, ref int index, char expected)
    {
        if (index >= value.Length || value[index] != expected)
            return false;
        index++;
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
    private static readonly string[] MonthNames =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    [GeneratedRegex(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
