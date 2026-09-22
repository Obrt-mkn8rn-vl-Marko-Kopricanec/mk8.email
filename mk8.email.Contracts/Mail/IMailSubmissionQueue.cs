namespace mk8.email.Contracts.Mail;

public sealed record MailDsnEnvelope(
    string? ReturnContent = null,
    string? EnvelopeId = null);

public sealed record MailDsnRecipient(
    string? Notify = null,
    string? OriginalRecipient = null);

public sealed record MailEnvelopeRecipient(
    string Address,
    bool IsLocal,
    MailDsnRecipient? Dsn = null);

public sealed record MailSubmission(
    Guid QueueId,
    string EnvelopeSender,
    IReadOnlyList<MailEnvelopeRecipient> Recipients,
    string RawMessage,
    string? ClientIp,
    string? Helo,
    string? AuthenticatedUser,
    bool RequiresSmtpUtf8 = false,
    MailDsnEnvelope? Dsn = null);

public interface IMailSubmissionQueue
{
    Task<Guid> EnqueueAsync(
        MailSubmission submission,
        CancellationToken cancellationToken = default);
}
