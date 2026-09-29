using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailSubmissionReader(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IMailSubmissionReader
{
    public async Task<MailSubmissionReadResult> ReadAsync(
        MailSubmissionReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (command.SubmissionIds?.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailSubmissionReadStatus.RequestTooLarge, null, []);
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailSubmissionReadStatus.AccountNotFound, null, []);

        var query = database.JmapEmailSubmissions.AsNoTracking()
            .Where(submission => submission.AccountId == account.InboxId);
        if (command.SubmissionIds is not null)
        {
            var ids = command.SubmissionIds.ToArray();
            query = query.Where(submission => ids.Contains(submission.Id));
        }
        var rows = await query.OrderBy(submission => submission.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (command.SubmissionIds is null && rows.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailSubmissionReadStatus.RequestTooLarge, null, []);

        var snapshots = new List<MailSubmissionSnapshot>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var recipients = command.IncludeDeliveryStatus
                ? await ReadDeliveryAsync(row.QueueId, cancellationToken).ConfigureAwait(false)
                : null;
            snapshots.Add(new(row.Id, row.IdentityId, row.EmailId, row.ThreadId,
                row.EnvelopeJson, row.EnvelopeSender, row.EnvelopeRecipients.ToArray(),
                row.SendAt, row.UndoStatus, recipients));
        }
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.EmailSubmissionDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailSubmissionReadStatus.Ok, state, snapshots);
    }

    private async Task<IReadOnlyList<MailSubmissionDeliverySnapshot>> ReadDeliveryAsync(
        Guid queueId,
        CancellationToken cancellationToken)
    {
        var recipients = await database.MailQueueRecipients.AsNoTracking()
            .Where(recipient => recipient.MessageId == queueId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new MailSubmissionDeliverySnapshot[recipients.Count];
        for (var index = 0; index < recipients.Count; index++)
        {
            var recipient = recipients[index];
            var state = recipient.State switch
            {
                MailQueueRecipientStates.Pending => MailSubmissionDeliveryState.Pending,
                MailQueueRecipientStates.Delivered when recipient.IsLocal => MailSubmissionDeliveryState.DeliveredLocal,
                MailQueueRecipientStates.Delivered => MailSubmissionDeliveryState.DeliveredRemote,
                MailQueueRecipientStates.PermanentFailure => MailSubmissionDeliveryState.PermanentFailure,
                MailQueueRecipientStates.Quarantined => MailSubmissionDeliveryState.Quarantined,
                _ => MailSubmissionDeliveryState.Unknown,
            };
            snapshots[index] = new(recipient.Recipient, state, recipient.LastError);
        }
        return snapshots;
    }
}
