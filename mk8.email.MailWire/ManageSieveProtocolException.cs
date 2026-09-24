namespace mk8.email.MailWire;

// This is a friend-assembly parser signal, not an externally catchable exception API.
#pragma warning disable CA1032, CA1064
internal sealed class ManageSieveProtocolException(
    string message,
    bool isFatal = false,
    string? responseCode = null) : Exception(message)
{
    public bool IsFatal { get; } = isFatal;
    public string? ResponseCode { get; } = responseCode;
}
#pragma warning restore CA1032, CA1064
