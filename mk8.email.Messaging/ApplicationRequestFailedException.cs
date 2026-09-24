namespace mk8.email.Messaging;

// A durable failure is meaningful only with its request ID and backend error code.
#pragma warning disable CA1032, RCS1194
public sealed class ApplicationRequestFailedException : Exception
{
    public ApplicationRequestFailedException(Guid requestId, string errorCode, string? errorDetail)
        : base(errorDetail ?? "The application request failed.")
    {
        RequestId = requestId;
        ErrorCode = errorCode;
    }

    public Guid RequestId { get; }
    public string ErrorCode { get; }
}
#pragma warning restore CA1032, RCS1194
