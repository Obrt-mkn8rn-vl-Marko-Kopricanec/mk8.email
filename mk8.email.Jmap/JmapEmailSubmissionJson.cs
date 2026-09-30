using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapEmailSubmissionJson
{
    public static readonly HashSet<string> Properties = new HashSet<string>(
        [
            "id", "identityId", "emailId", "threadId", "envelope", "sendAt",
            "undoStatus", "deliveryStatus", "dsnBlobIds", "mdnBlobIds",
        ],
        StringComparer.Ordinal);

    public static async Task<JsonObject> BuildAsync(
        EmailDbContext database,
        JmapEmailSubmissionDB submission,
        IReadOnlySet<string>? properties,
        CancellationToken cancellationToken)
    {
        var result = new JsonObject { ["id"] = JmapId.Submission(submission.Id) };
        if (Wants("identityId")) result["identityId"] = submission.IdentityId;
        if (Wants("emailId")) result["emailId"] = submission.EmailId;
        if (Wants("threadId")) result["threadId"] = submission.ThreadId;
        if (Wants("envelope"))
            result["envelope"] = BuildEnvelope(submission);
        if (Wants("sendAt")) result["sendAt"] = JmapDate.FormatUtc(submission.SendAt);
        if (Wants("undoStatus")) result["undoStatus"] = submission.UndoStatus;
        if (Wants("deliveryStatus"))
            result["deliveryStatus"] = await DeliveryStatusAsync(database, submission, cancellationToken).ConfigureAwait(false);
        if (Wants("dsnBlobIds")) result["dsnBlobIds"] = new JsonArray();
        if (Wants("mdnBlobIds")) result["mdnBlobIds"] = new JsonArray();
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    private static JsonObject BuildEnvelope(JmapEmailSubmissionDB submission)
    {
        if (submission.EnvelopeJson is not null)
        {
            try
            {
                if (JsonNode.Parse(submission.EnvelopeJson) is JsonObject stored)
                    return stored;
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through for rows created before the canonical envelope
                // column existed or for a damaged optional cache value.
            }
        }

        var recipients = new JsonArray();
        foreach (var recipient in submission.EnvelopeRecipients)
        {
            recipients.Add(new JsonObject
            {
                ["email"] = recipient,
                ["parameters"] = null,
            });
        }
        return new JsonObject
        {
            ["mailFrom"] = new JsonObject
            {
                ["email"] = submission.EnvelopeSender,
                ["parameters"] = null,
            },
            ["rcptTo"] = recipients,
        };
    }

    private static async Task<JsonNode?> DeliveryStatusAsync(
        EmailDbContext database,
        JmapEmailSubmissionDB submission,
        CancellationToken cancellationToken)
    {
        var recipients = await database.MailQueueRecipients
            .AsNoTracking()
            .Where(recipient => recipient.MessageId == submission.QueueId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (recipients.Count == 0)
            return null;
        var result = new JsonObject();
        for (var recipientIndex = 0; recipientIndex < recipients.Count; recipientIndex++)
        {
            var recipient = recipients[recipientIndex];
            var delivered = recipient.State switch
            {
                MailQueueRecipientStates.Pending => "queued",
                MailQueueRecipientStates.Delivered when recipient.IsLocal => "yes",
                MailQueueRecipientStates.Delivered => "unknown",
                MailQueueRecipientStates.PermanentFailure => "no",
                MailQueueRecipientStates.Quarantined => "no",
                _ => "unknown",
            };
            var reply = recipient.State switch
            {
                MailQueueRecipientStates.Pending => "250 2.0.0 Queued for delivery",
                MailQueueRecipientStates.Delivered => "250 2.0.0 Delivery accepted",
                MailQueueRecipientStates.PermanentFailure =>
                    $"550 5.0.0 {recipient.LastError ?? "Permanent delivery failure"}",
                MailQueueRecipientStates.Quarantined =>
                    $"550 5.7.1 {recipient.LastError ?? "Message quarantined"}",
                _ => "250 2.0.0 Delivery status unknown",
            };
            result[recipient.Recipient] = new JsonObject
            {
                ["smtpReply"] = reply.Replace('\r', ' ').Replace('\n', ' '),
                ["delivered"] = delivered,
                ["displayed"] = "unknown",
            };
        }
        return result;
    }
}
