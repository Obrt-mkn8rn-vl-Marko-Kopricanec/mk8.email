namespace mk8.email.Messaging;

// A timeout must identify the durable request whose deadline elapsed.
#pragma warning disable CA1032, RCS1194
public sealed class ApplicationRequestExpiredException : TimeoutException
{
    public ApplicationRequestExpiredException(Guid requestId)
        : base("The application request expired before a response was available.")
    {
        RequestId = requestId;
    }

    public Guid RequestId { get; }
}
#pragma warning restore CA1032, RCS1194
