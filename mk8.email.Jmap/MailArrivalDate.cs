namespace mk8.email.Jmap;

internal static class MailArrivalDate
{
    public static DateTime DetermineReceivedAt(byte[] raw)
    {
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            foreach (var header in message.Headers.Where(header =>
                         header.Field.Equals("Received", StringComparison.OrdinalIgnoreCase)))
            {
                var separator = header.Value.LastIndexOf(';');
                if (separator >= 0
                    && MimeKit.Utils.DateUtils.TryParse(
                        header.Value[(separator + 1)..],
                        out var date))
                {
                    return date.UtcDateTime;
                }
            }
            return DateTime.UtcNow;
        }
        catch (FormatException)
        {
            return DateTime.UtcNow;
        }
    }
}
