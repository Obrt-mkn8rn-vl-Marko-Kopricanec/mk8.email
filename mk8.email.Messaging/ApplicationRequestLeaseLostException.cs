namespace mk8.email.Messaging;

// Lease loss must identify the request whose ownership changed.
#pragma warning disable CA1032, RCS1194
public sealed class ApplicationRequestLeaseLostException : InvalidOperationException
{
    public ApplicationRequestLeaseLostException(Guid requestId)
        : base("The application request lease is no longer owned by this worker.")
    {
        RequestId = requestId;
    }

    public Guid RequestId { get; }
}
#pragma warning restore CA1032, RCS1194
