namespace mk8.email.Application;

internal static class MailCaseNormalization
{
    // Stored mailbox keys and several protocol tokens require lowercase output.
    internal static string ToMailLowerInvariant(this string value)
    {
#pragma warning disable CA1308
        return value.ToLowerInvariant();
#pragma warning restore CA1308
    }
}
