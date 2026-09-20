using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapEmailSubmissionJson
{
    public static readonly IReadOnlySet<string> Properties = new HashSet<string>(
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
        if (Wants("sendAt")) result["sendAt"] = JmapEmailCodec.FormatUtcDate(submission.SendAt);
        if (Wants("undoStatus")) result["undoStatus"] = submission.UndoStatus;
        if (Wants("deliveryStatus"))
            result["deliveryStatus"] = await DeliveryStatusAsync(database, submission, cancellationToken);
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
            .ToListAsync(cancellationToken);
        if (recipients.Count == 0)
            return null;
        var result = new JsonObject();
        foreach (var recipient in recipients)
        {
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

internal sealed class EmailSubmissionGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "EmailSubmission/get";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var requestedProperties)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", context, true, out var requestedIds))
            return JmapMethodResponse.Error("invalidArguments");
        var properties = requestedProperties?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !JmapEmailSubmissionJson.Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var all = await database.JmapEmailSubmissions
            .AsNoTracking()
            .Where(submission => submission.AccountId == account.InboxId)
            .OrderBy(submission => submission.CreatedAt)
            .ToListAsync(cancellationToken);
        if (requestedIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = all.ToDictionary(submission => JmapId.Submission(submission.Id), StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var submission))
                list.Add(await JmapEmailSubmissionJson.BuildAsync(database, submission, properties, cancellationToken));
            else
                notFound.Add(id);
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(account.InboxId, JmapConstants.EmailSubmissionDataType, cancellationToken),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}

internal sealed class EmailSubmissionChangesMethod(
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "EmailSubmission/changes";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "sinceState", "maxChanges")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0)
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.EmailSubmissionDataType,
            sinceState,
            maxChanges,
            environment.Jmap.MaxObjectsInGet,
            cancellationToken);
        if (changes is null) return JmapMethodResponse.Error("cannotCalculateChanges");
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = changes.OldState,
            ["newState"] = changes.NewState,
            ["hasMoreChanges"] = changes.HasMoreChanges,
            ["created"] = JmapMethodHelpers.ToJsonArray(changes.Created),
            ["updated"] = JmapMethodHelpers.ToJsonArray(changes.Updated),
            ["destroyed"] = JmapMethodHelpers.ToJsonArray(changes.Destroyed),
        });
    }
}
