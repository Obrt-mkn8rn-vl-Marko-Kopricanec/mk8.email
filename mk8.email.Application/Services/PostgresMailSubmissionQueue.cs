using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class PostgresMailSubmissionQueue(
    EmailDbContext database,
    EnvironmentConfig environment) : IMailSubmissionQueue
{
    public async Task<Guid> EnqueueAsync(
        MailSubmission submission,
        CancellationToken cancellationToken = default)
    {
        if (submission.QueueId == Guid.Empty)
            throw new ArgumentException("The queue identifier is not valid.", nameof(submission));

        if (!SmtpAddress.TryNormalize(
                submission.EnvelopeSender,
                allowEmpty: submission.AuthenticatedUser is null,
                out var sender))
        {
            throw new ArgumentException("The envelope sender is not valid.", nameof(submission));
        }

        if (submission.Recipients.Count is 0
            || submission.Recipients.Count > environment.Limits.MaxRecipientsPerMessage)
        {
            throw new ArgumentException("The recipient count is not valid.", nameof(submission));
        }

        if (MailWireEncoding.Instance.GetByteCount(submission.RawMessage) > environment.Limits.MaxMessageSizeBytes)
            throw new ArgumentException("The message is larger than the configured limit.", nameof(submission));

        var recipients = new List<MailEnvelopeRecipient>();
        foreach (var recipient in submission.Recipients)
        {
            if (!SmtpAddress.TryNormalize(recipient.Address, allowEmpty: false, out var address))
                throw new ArgumentException("A recipient address is not valid.", nameof(submission));

            if (recipients.All(item => !string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
                recipients.Add(new MailEnvelopeRecipient(address, recipient.IsLocal));
        }

        var authenticatedUser = submission.AuthenticatedUser;
        if (authenticatedUser is not null
            && !SmtpAddress.TryNormalize(authenticatedUser, allowEmpty: false, out authenticatedUser))
        {
            throw new ArgumentException("The authenticated user is not valid.", nameof(submission));
        }

        var now = DateTime.UtcNow;
        var message = new MailQueueMessageDB
        {
            Id = submission.QueueId,
            EnvelopeSender = sender,
            RawMessage = submission.RawMessage,
            ClientIp = NormalizeMetadata(submission.ClientIp, 45),
            Helo = NormalizeMetadata(submission.Helo, 255),
            AuthenticatedUser = authenticatedUser,
            Direction = authenticatedUser is null
                ? MailQueueDirections.Inbound
                : MailQueueDirections.Submission,
            State = MailQueueStates.Pending,
            ScanState = MailQueueScanStates.Pending,
            ReceivedAt = now,
            NextAttemptAt = now,
        };

        foreach (var recipient in recipients)
        {
            message.Recipients.Add(new MailQueueRecipientDB
            {
                Id = Guid.CreateVersion7(),
                Recipient = recipient.Address,
                IsLocal = recipient.IsLocal,
                State = MailQueueRecipientStates.Pending,
                NextAttemptAt = now,
                RedirectHistory = [recipient.Address],
            });
        }

        database.MailQueueMessages.Add(message);
        await database.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    private static string? NormalizeMetadata(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.ContainsAny(['\r', '\n', '\0']))
            return null;

        return normalized;
    }
}
