using System.Globalization;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapDateCodec
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
}
