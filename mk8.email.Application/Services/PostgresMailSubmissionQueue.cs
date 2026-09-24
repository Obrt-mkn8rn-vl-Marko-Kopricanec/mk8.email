using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class PostgresMailSubmissionQueue(
    EmailDbContext database,
    EnvironmentConfig environment,
    MailQueueContentService content,
    LargeObjectTransactionEffects transactionEffects,
    ILogger<PostgresMailSubmissionQueue> logger) : IMailSubmissionQueue
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
                out var sender,
                out var senderRequiresSmtpUtf8))
        {
            throw new ArgumentException("The envelope sender is not valid.", nameof(submission));
        }

        if (submission.Recipients.Count is 0
            || submission.Recipients.Count > environment.Limits.MaxRecipientsPerMessage)
        {
            throw new ArgumentException("The recipient count is not valid.", nameof(submission));
        }

        if (submission.RawMessage.Any(character => character > byte.MaxValue))
            throw new ArgumentException("The message is not in the mail wire byte representation.", nameof(submission));
        if (SmtpInternationalization.HeadersRequireSmtpUtf8(submission.RawMessage)
            && !SmtpInternationalization.HasValidUtf8Headers(submission.RawMessage))
        {
            throw new ArgumentException("Internationalized headers are not valid UTF-8.", nameof(submission));
        }

        if (MailWireEncoding.Instance.GetByteCount(submission.RawMessage) > environment.Limits.MaxMessageSizeBytes)
            throw new ArgumentException("The message is larger than the configured limit.", nameof(submission));

        var recipients = new List<MailEnvelopeRecipient>();
        var recipientRequiresSmtpUtf8 = false;
        foreach (var recipient in submission.Recipients)
        {
            if (!SmtpAddress.TryNormalize(
                    recipient.Address,
                    allowEmpty: false,
                    out var address,
                    out var addressRequiresSmtpUtf8))
            {
                throw new ArgumentException("A recipient address is not valid.", nameof(submission));
            }

            recipientRequiresSmtpUtf8 |= addressRequiresSmtpUtf8;

            if (recipients.All(item => !string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
                recipients.Add(new MailEnvelopeRecipient(address, recipient.IsLocal, recipient.Dsn));
        }

        var authenticatedUser = submission.AuthenticatedUser;
        if (authenticatedUser is not null
            && !SmtpAddress.TryNormalize(authenticatedUser, allowEmpty: false, out authenticatedUser))
        {
            throw new ArgumentException("The authenticated user is not valid.", nameof(submission));
        }

        string? dsnReturnContent = null;
        string? dsnEnvelopeId = null;
        if (submission.Dsn?.ReturnContent is not null
            && !SmtpDsn.TryNormalizeReturnContent(
                submission.Dsn.ReturnContent,
                out dsnReturnContent))
        {
            throw new ArgumentException("The DSN return-content request is not valid.", nameof(submission));
        }
        if (submission.Dsn?.EnvelopeId is not null)
        {
            if (!SmtpDsn.TryValidateEnvelopeId(submission.Dsn.EnvelopeId))
                throw new ArgumentException("The DSN envelope identifier is not valid.", nameof(submission));
            dsnEnvelopeId = submission.Dsn.EnvelopeId;
        }

        var now = DateTime.UtcNow;
        var message = new MailQueueMessageDB
        {
            Id = submission.QueueId,
            EnvelopeSender = sender,
            RequiresSmtpUtf8 = submission.RequiresSmtpUtf8
                || senderRequiresSmtpUtf8
                || recipientRequiresSmtpUtf8
                || SmtpInternationalization.HeadersRequireSmtpUtf8(submission.RawMessage),
            DsnReturnContent = dsnReturnContent,
            DsnEnvelopeId = dsnEnvelopeId,
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
            string? dsnNotify = null;
            string? dsnOriginalRecipient = null;
            if (recipient.Dsn?.Notify is not null
                && !SmtpDsn.TryNormalizeNotify(recipient.Dsn.Notify, out dsnNotify))
            {
                throw new ArgumentException("A DSN notification request is not valid.", nameof(submission));
            }
            if (recipient.Dsn?.OriginalRecipient is not null)
            {
                if (!SmtpDsn.TryValidateOriginalRecipient(
                        recipient.Dsn.OriginalRecipient,
                        message.RequiresSmtpUtf8,
                        out _,
                        out _))
                {
                    throw new ArgumentException("A DSN original recipient is not valid.", nameof(submission));
                }
                dsnOriginalRecipient = recipient.Dsn.OriginalRecipient;
            }

            message.Recipients.Add(new MailQueueRecipientDB
            {
                Id = Guid.CreateVersion7(),
                Recipient = recipient.Address,
                IsLocal = recipient.IsLocal,
                State = MailQueueRecipientStates.Pending,
                NextAttemptAt = now,
                RedirectHistory = [recipient.Address],
                DsnNotify = dsnNotify,
                DsnOriginalRecipient = dsnOriginalRecipient,
            });
        }

        var effectMarker = transactionEffects.Mark();
        IDbContextTransaction? transaction = null;
        var commitAttempted = false;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await content.SetAsync(message, submission.RawMessage, cancellationToken).ConfigureAwait(false);
            await database.MailQueueMessages.AddAsync(message, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await transactionEffects.CommitAsync(effectMarker).ConfigureAwait(false);
            return message.Id;
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    logger.LogWarning(
                        rollbackException,
                        "Could not roll back queue submission {QueueId}",
                        message.Id);
                }
            }
            if (commitAttempted)
                transactionEffects.Discard(effectMarker);
            else
                await transactionEffects.RollbackAsync(effectMarker).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
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
