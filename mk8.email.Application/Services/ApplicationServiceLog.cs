using Microsoft.Extensions.Logging;

namespace mk8.email.Application.Services;

internal static partial class ApplicationServiceLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "Could not delete unreferenced DAV object {ObjectName}")]
    public static partial void DavObjectDeleteFailed(ILogger logger, Exception exception, string objectName);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "Could not roll back DAV resource migration for {ResourceId}")]
    public static partial void DavMigrationRollbackFailed(ILogger logger, Exception exception, Guid resourceId);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "Could not clean up DAV migration object {ObjectName}")]
    public static partial void DavMigrationCleanupFailed(ILogger logger, Exception exception, string objectName);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning,
        Message = "Could not roll back IMAP mailbox deletion")]
    public static partial void ImapMailboxDeletionRollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning,
        Message = "Could not roll back IMAP expunge")]
    public static partial void ImapExpungeRollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning,
        Message = "Could not roll back IMAP COPY")]
    public static partial void ImapCopyRollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning,
        Message = "Could not roll back IMAP APPEND")]
    public static partial void ImapAppendRollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning,
        Message = "Could not delete transactional large object {ObjectName}")]
    public static partial void TransactionalObjectDeleteFailed(ILogger logger, Exception exception, string objectName);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Warning,
        Message = "Could not delete unreferenced mail queue object {ObjectName}")]
    public static partial void QueueObjectDeleteFailed(ILogger logger, Exception exception, string objectName);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Warning,
        Message = "Could not roll back queue content migration for {QueueId}")]
    public static partial void QueueMigrationRollbackFailed(ILogger logger, Exception exception, Guid queueId);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Warning,
        Message = "Could not clean up queue migration object {ObjectName}")]
    public static partial void QueueMigrationCleanupFailed(ILogger logger, Exception exception, string objectName);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Error,
        Message = "The mail queue worker failed.")]
    public static partial void QueueWorkerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Error,
        Message = "The mail queue notification listener failed.")]
    public static partial void QueueNotificationListenerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4013, Level = LogLevel.Error,
        Message = "Queue processing failed for {QueueId}")]
    public static partial void QueueProcessingFailed(ILogger logger, Exception exception, Guid queueId);

    [LoggerMessage(EventId = 4014, Level = LogLevel.Error,
        Message = "The queue processing lease was lost for {QueueId}")]
    public static partial void QueueProcessingLeaseLost(ILogger logger, Guid queueId);

    [LoggerMessage(EventId = 4015, Level = LogLevel.Error,
        Message = "Could not renew the queue lease for {QueueId}")]
    public static partial void QueueLeaseRenewalFailed(ILogger logger, Exception exception, Guid queueId);

    [LoggerMessage(EventId = 4016, Level = LogLevel.Warning,
        Message = "Queue message {QueueId} was quarantined")]
    public static partial void QueueMessageQuarantined(ILogger logger, Guid queueId);

    [LoggerMessage(EventId = 4017, Level = LogLevel.Warning,
        Message = "Abandoned the success DSN for queue recipient {RecipientId} after queue expiry")]
    public static partial void SuccessDsnAbandoned(ILogger logger, Guid recipientId);

    [LoggerMessage(EventId = 4018, Level = LogLevel.Warning,
        Message = "Abandoned the failure DSN for queue recipient {RecipientId} after queue expiry")]
    public static partial void FailureDsnAbandoned(ILogger logger, Guid recipientId);

    [LoggerMessage(EventId = 4019, Level = LogLevel.Information,
        Message = "Removed {Count} completed queue records")]
    public static partial void CompletedQueueRecordsRemoved(ILogger logger, int count);

    [LoggerMessage(EventId = 4020, Level = LogLevel.Warning,
        Message = "Could not roll back completed queue cleanup")]
    public static partial void CompletedQueueCleanupRollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Warning,
        Message = "Could not roll back mailbox message migration for {EmailId}")]
    public static partial void MailboxMigrationRollbackFailed(ILogger logger, Exception exception, Guid emailId);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Warning,
        Message = "Could not clean up mailbox migration object {ObjectName}")]
    public static partial void MailboxMigrationCleanupFailed(ILogger logger, Exception exception, string objectName);

    [LoggerMessage(EventId = 4023, Level = LogLevel.Warning,
        Message = "Could not roll back POP3 deletion")]
    public static partial void Pop3DeletionRollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4024, Level = LogLevel.Warning,
        Message = "Could not roll back queue submission {QueueId}")]
    public static partial void QueueSubmissionRollbackFailed(ILogger logger, Exception exception, Guid queueId);

    [LoggerMessage(EventId = 4025, Level = LogLevel.Error,
        Message = "Active Sieve script {ScriptId} is invalid at line {Line}, column {Column}: {Message}")]
    public static partial void ActiveSieveScriptInvalid(
        ILogger logger, Guid scriptId, int line, int column, string message);

    [LoggerMessage(EventId = 4026, Level = LogLevel.Warning,
        Message = "Sieve script {ScriptId} attempted delivery to an unavailable mailbox; applying implicit keep")]
    public static partial void SieveMailboxUnavailable(ILogger logger, Guid scriptId);

    [LoggerMessage(EventId = 4027, Level = LogLevel.Error,
        Message = "Sieve evaluation failed for script {ScriptId}")]
    public static partial void SieveEvaluationFailed(ILogger logger, Exception exception, Guid scriptId);

    [LoggerMessage(EventId = 4028, Level = LogLevel.Warning,
        Message = "Could not roll back Sieve script migration for {ScriptId}")]
    public static partial void SieveMigrationRollbackFailed(ILogger logger, Exception exception, Guid scriptId);

    [LoggerMessage(EventId = 4029, Level = LogLevel.Warning,
        Message = "Could not roll back a Sieve script content transaction")]
    public static partial void SieveContentTransactionRollbackFailed(ILogger logger, Exception exception);
}
