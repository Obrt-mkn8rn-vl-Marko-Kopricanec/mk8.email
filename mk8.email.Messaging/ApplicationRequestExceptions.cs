namespace mk8.email.Messaging;

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

public sealed class ApplicationRequestExpiredException : TimeoutException
{
    public ApplicationRequestExpiredException(Guid requestId)
        : base("The application request expired before a response was available.")
    {
        RequestId = requestId;
    }

    public Guid RequestId { get; }
}

public sealed class ApplicationRequestLeaseLostException : InvalidOperationException
{
    public ApplicationRequestLeaseLostException(Guid requestId)
        : base("The application request lease is no longer owned by this worker.")
    {
        RequestId = requestId;
    }

    public Guid RequestId { get; }
}
