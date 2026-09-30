using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class MailSubmissionSnapshots
{
    public static async Task<MailSubmissionSnapshot> ReadAsync(EmailDbContext database, JmapEmailSubmissionDB row,
        bool includeDelivery, CancellationToken cancellationToken)
    {
        IReadOnlyList<MailSubmissionDeliverySnapshot>? snapshots = null;
        if (includeDelivery)
        {
            var recipients = await database.MailQueueRecipients.AsNoTracking()
                .Where(recipient => recipient.MessageId == row.QueueId).ToListAsync(cancellationToken).ConfigureAwait(false);
            snapshots = recipients.Select(recipient => new MailSubmissionDeliverySnapshot(recipient.Recipient,
                recipient.State switch
                {
                    MailQueueRecipientStates.Pending => MailSubmissionDeliveryState.Pending,
                    MailQueueRecipientStates.Delivered when recipient.IsLocal => MailSubmissionDeliveryState.DeliveredLocal,
                    MailQueueRecipientStates.Delivered => MailSubmissionDeliveryState.DeliveredRemote,
                    MailQueueRecipientStates.PermanentFailure => MailSubmissionDeliveryState.PermanentFailure,
                    MailQueueRecipientStates.Quarantined => MailSubmissionDeliveryState.Quarantined,
                    _ => MailSubmissionDeliveryState.Unknown,
                }, recipient.LastError)).ToArray();
        }
        return new(row.Id, row.IdentityId, row.EmailId, row.ThreadId, MailSubmissionEnvelopeCache.Read(row),
            row.EnvelopeSender, row.EnvelopeRecipients.ToArray(), row.SendAt, row.UndoStatus, snapshots);
    }
}
