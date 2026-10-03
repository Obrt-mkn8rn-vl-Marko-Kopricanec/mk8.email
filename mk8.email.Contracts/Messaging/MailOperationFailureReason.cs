namespace mk8.email.Contracts.Messaging;

public enum MailOperationFailureReason
{
    None = 0,
    NotSupported = 1,
    InternalFailure = 2,
    PartiallyCompleted = 3,
}
