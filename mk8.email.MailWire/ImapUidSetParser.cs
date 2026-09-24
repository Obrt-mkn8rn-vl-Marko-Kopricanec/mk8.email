using System.Globalization;

namespace mk8.email.MailWire;

public static class ImapUidSetParser
{
    // Preserve the published mutable output list consumed by existing protocol callers.
#pragma warning disable CA1002, MA0016
    public static bool TryParse(string value, out List<ImapUidSetRange> ranges)
    {
        ranges = [];
        if (string.IsNullOrEmpty(value))
            return false;

        foreach (var part in value.Split(','))
        {
            if (part.Length == 0)
                return false;

            var separator = part.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                if (!TryParseEndpoint(part, out var endpoint))
                    return false;
                ranges.Add(new ImapUidSetRange(endpoint, endpoint));
                continue;
            }

            if (separator == 0 || separator == part.Length - 1
                || part.IndexOf(':', separator + 1) >= 0
                || !TryParseEndpoint(part[..separator], out var start)
                || !TryParseEndpoint(part[(separator + 1)..], out var end))
            {
                return false;
            }
            ranges.Add(new ImapUidSetRange(start, end));
        }

        return true;
    }
#pragma warning restore CA1002, MA0016

    private static bool TryParseEndpoint(string value, out int? endpoint)
    {
        if (string.Equals(value, "*", StringComparison.Ordinal))
        {
            endpoint = null;
            return true;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                endpoint = null;
                return false;
            }
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            endpoint = parsed;
            return true;
        }

        endpoint = null;
        return false;
    }
}
