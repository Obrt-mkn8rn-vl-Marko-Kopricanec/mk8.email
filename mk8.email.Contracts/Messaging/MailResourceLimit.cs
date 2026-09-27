namespace mk8.email.Contracts.Messaging;

public enum MailResourceLimit
{
    None = 0,
    OperationCount = 1,
    RequestConcurrency = 2,
    UploadConcurrency = 3,
    UploadSize = 4,
}
