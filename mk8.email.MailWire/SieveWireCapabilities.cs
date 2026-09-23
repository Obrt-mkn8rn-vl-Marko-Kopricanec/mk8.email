namespace mk8.email.MailWire;

public static class SieveWireCapabilities
{
    public const int MaximumScriptBytes = 1024 * 1024;
    public const int MaximumRedirects = 100;

    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "body",
            "comparator-i;ascii-casemap",
            "copy",
            "envelope",
            "fileinto",
            "imap4flags",
            "mailbox",
            "reject",
        };
}
