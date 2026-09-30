using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewaySubmissionGetCodec
{
    private static readonly HashSet<string> AllowedProperties = new(
        ["id", "identityId", "emailId", "threadId", "envelope", "sendAt",
         "undoStatus", "deliveryStatus", "dsnBlobIds", "mdnBlobIds"],
        StringComparer.Ordinal);

    internal sealed record Call(
        MailSubmissionReadCommand Command,
        string AccountId,
        IReadOnlyList<string>? RequestedIds,
        IReadOnlySet<string>? Properties,
        int MaximumObjects);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "ids" or "properties"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || !TryParseIds(arguments, out var requestedIds, out var submissionIds)
            || !TryParseProperties(arguments, out var properties))
        {
            failure = "invalidArguments";
            return false;
        }
        if (requestedIds?.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        if (accountId.Length != 33 || accountId[0] != 'A'
            || !Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }
        var includeDelivery = properties is null || properties.Contains("deliveryStatus");
        call = new(new(accountGuid, requestedIds is null ? null : submissionIds, includeDelivery),
            accountId, requestedIds, properties, maximumObjects);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailSubmissionReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Submissions is null)
            throw new InvalidOperationException("The Application returned an invalid submission read result.");
        if (result.Status == MailSubmissionReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailSubmissionReadStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.State is null || result.Submissions.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned an incomplete submission read result.");

        var byId = new Dictionary<string, MailSubmissionSnapshot>(StringComparer.Ordinal);
        foreach (var submission in result.Submissions)
        {
            if (submission is null || submission.Id == Guid.Empty
                || submission.IdentityId is null || submission.EmailId is null
                || submission.ThreadId is null || submission.EnvelopeSender is null
                || submission.EnvelopeRecipients is null || submission.UndoStatus is null
                || !byId.TryAdd(FormatId(submission.Id), submission))
                throw new InvalidOperationException("The Application returned invalid submission data.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var submission))
                list.Add(Build(submission, call.Properties));
            else
                notFound.Add(id);
        }
        return (MailOperationKind.ReadSubmissions, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    internal static JsonObject Build(MailSubmissionSnapshot submission, IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = FormatId(submission.Id) };
        if (Wants("identityId")) result["identityId"] = submission.IdentityId;
        if (Wants("emailId")) result["emailId"] = submission.EmailId;
        if (Wants("threadId")) result["threadId"] = submission.ThreadId;
        if (Wants("envelope")) result["envelope"] = BuildEnvelope(submission);
        if (Wants("sendAt")) result["sendAt"] = GatewayJmapDateCodec.FormatUtc(submission.SendAt);
        if (Wants("undoStatus")) result["undoStatus"] = submission.UndoStatus;
        if (Wants("deliveryStatus")) result["deliveryStatus"] = BuildDeliveryStatus(submission.DeliveryRecipients);
        if (Wants("dsnBlobIds")) result["dsnBlobIds"] = new JsonArray();
        if (Wants("mdnBlobIds")) result["mdnBlobIds"] = new JsonArray();
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    private static JsonObject BuildEnvelope(MailSubmissionSnapshot submission)
    {
        if (submission.EnvelopeJson is not null)
        {
            try
            {
                if (JsonNode.Parse(submission.EnvelopeJson) is JsonObject stored)
                    return stored;
            }
            catch (JsonException)
            {
                // Older or damaged optional cache values use the indexed envelope.
            }
        }
        var recipients = new JsonArray();
        for (var index = 0; index < submission.EnvelopeRecipients.Count; index++)
        {
            if (submission.EnvelopeRecipients[index] is null)
                throw new InvalidOperationException("The Application returned an invalid envelope recipient.");
            recipients.Add(new JsonObject
            {
                ["email"] = submission.EnvelopeRecipients[index],
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

    private static JsonObject? BuildDeliveryStatus(IReadOnlyList<MailSubmissionDeliverySnapshot>? recipients)
    {
        if (recipients is null)
            throw new InvalidOperationException("The Application omitted requested submission delivery data.");
        if (recipients.Count == 0)
            return null;
        var result = new JsonObject();
        for (var index = 0; index < recipients.Count; index++)
        {
            var recipient = recipients[index];
            if (recipient is null || recipient.Recipient is null || !Enum.IsDefined(recipient.State))
                throw new InvalidOperationException("The Application returned invalid delivery data.");
            var delivered = recipient.State switch
            {
                MailSubmissionDeliveryState.Pending => "queued",
                MailSubmissionDeliveryState.DeliveredLocal => "yes",
                MailSubmissionDeliveryState.PermanentFailure or MailSubmissionDeliveryState.Quarantined => "no",
                _ => "unknown",
            };
            var reply = recipient.State switch
            {
                MailSubmissionDeliveryState.Pending => "250 2.0.0 Queued for delivery",
                MailSubmissionDeliveryState.DeliveredLocal or MailSubmissionDeliveryState.DeliveredRemote =>
                    "250 2.0.0 Delivery accepted",
                MailSubmissionDeliveryState.PermanentFailure =>
                    $"550 5.0.0 {recipient.LastError ?? "Permanent delivery failure"}",
                MailSubmissionDeliveryState.Quarantined =>
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

    private static bool TryParseProperties(JsonObject arguments, out FrozenSet<string>? properties)
    {
        properties = null;
        if (!arguments.TryGetPropertyValue("properties", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name)
                || name is null || !AllowedProperties.Contains(name))
                return false;
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static bool TryParseIds(JsonObject arguments, out string[]? ids, out Guid[] submissionIds)
    {
        ids = null;
        submissionIds = [];
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var requested = new List<string>(array.Count);
        var parsed = new List<Guid>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            requested.Add(id);
            if (id.Length == 33 && id[0] == 'S'
                && Guid.TryParseExact(id.AsSpan(1), "N", out var submissionId)
                && string.Equals(id, FormatId(submissionId), StringComparison.Ordinal))
                parsed.Add(submissionId);
        }
        ids = requested.ToArray();
        submissionIds = parsed.ToArray();
        return true;
    }

    private static string FormatId(Guid id) => $"S{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
