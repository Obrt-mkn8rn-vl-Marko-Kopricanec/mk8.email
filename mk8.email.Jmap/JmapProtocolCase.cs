namespace mk8.email.Jmap;

internal static class JmapProtocolCase
{
    internal static string ToProtocolLowerInvariant(this string value)
    {
        // JMAP, MIME, vCard, and hexadecimal wire tokens require lowercase output.
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToLower(value);
    }
}
