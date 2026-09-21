namespace mk8.email.Application.Interfaces;

public sealed record MailEnvelopeRecipient(string Address, bool IsLocal);

public sealed record MailSubmission(
    Guid QueueId,
    string EnvelopeSender,
    IReadOnlyList<MailEnvelopeRecipient> Recipients,
    string RawMessage,
    string? ClientIp,
    string? Helo,
    string? AuthenticatedUser,
    bool RequiresSmtpUtf8 = false);

public interface IMailSubmissionQueue
{
    Task<Guid> EnqueueAsync(
        MailSubmission submission,
        CancellationToken cancellationToken = default);
}
