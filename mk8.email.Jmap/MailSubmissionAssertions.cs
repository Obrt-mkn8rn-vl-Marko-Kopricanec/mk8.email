using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailSubmissionAssertions
{
    public static MailSubmissionMutationFailure? Verify(MailSubmissionSnapshot snapshot, MailSubmissionPatch patch)
    {
        if (patch.Failure is not null) return patch.Failure;
        var invalid = new List<string>();
        var undo = snapshot.UndoStatus;
        foreach (var assertion in patch.Assertions)
        {
            var original = Observe(snapshot, assertion.Field);
            if (!MailMessageAssertions.TryApply(original, assertion.Changes, out var revised))
                return Failure(MailSubmissionMutationError.InvalidPatch);
            if (assertion.Field == MailSubmissionObservationField.UndoState)
                undo = revised is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            else if (assertion.Field == MailSubmissionObservationField.Unknown || !JsonNode.DeepEquals(original, revised))
                invalid.Add(assertion.Label);
        }
        if (invalid.Count > 0) return new(MailSubmissionMutationError.InvalidProperties, null, invalid.Order(StringComparer.Ordinal).ToArray(), null, null, null);
        if (undo is not ("pending" or "final" or "canceled"))
            return new(MailSubmissionMutationError.InvalidProperties, null, ["undoStatus"], null, null, null);
        return !string.Equals(undo, snapshot.UndoStatus, StringComparison.Ordinal)
            || string.Equals(undo, "canceled", StringComparison.Ordinal) && !string.Equals(snapshot.UndoStatus, "pending", StringComparison.Ordinal)
            ? Failure(MailSubmissionMutationError.CannotUnsend) : null;
    }

    private static JsonNode? Observe(MailSubmissionSnapshot value, MailSubmissionObservationField field) => field switch
    {
        MailSubmissionObservationField.Unknown => null,
        MailSubmissionObservationField.Id => JsonValue.Create(JmapId.Submission(value.Id)),
        MailSubmissionObservationField.IdentityReference => JsonValue.Create(value.IdentityId),
        MailSubmissionObservationField.MessageReference => JsonValue.Create(value.EmailId),
        MailSubmissionObservationField.ThreadReference => JsonValue.Create(value.ThreadId),
        MailSubmissionObservationField.Envelope => Envelope(value.Envelope!),
        MailSubmissionObservationField.SendTime => JsonValue.Create(JmapDate.FormatUtc(value.SendAt)),
        MailSubmissionObservationField.UndoState => JsonValue.Create(value.UndoStatus),
        MailSubmissionObservationField.Delivery => Delivery(value.DeliveryRecipients!),
        MailSubmissionObservationField.DeliveryReceipts or MailSubmissionObservationField.ReadReceipts => new JsonArray(),
        _ => throw new InvalidOperationException("The submission observation is invalid."),
    };

    private static JsonObject Envelope(MailSubmissionEnvelope value) => new()
    {
        ["Sender"] = Address(value.Sender),
        ["Recipients"] = new JsonArray(value.Recipients.Select(Address).Cast<JsonNode?>().ToArray()),
    };

    private static JsonObject Address(MailEnvelopeAddress value)
    {
        JsonObject? parameters = null;
        if (value.Parameters is not null)
        {
            parameters = new();
            foreach (var item in value.Parameters) parameters[item.Key] = item.Value;
        }
        return new() { ["Address"] = value.Address, ["Parameters"] = parameters };
    }

    private static JsonObject? Delivery(IReadOnlyList<MailSubmissionDeliverySnapshot> recipients)
    {
        if (recipients.Count == 0) return null;
        var result = new JsonObject();
        foreach (var recipient in recipients)
            result[recipient.Recipient] = new JsonObject
            {
                ["Reply"] = Reply(recipient),
                ["Delivery"] = recipient.State switch
                {
                    MailSubmissionDeliveryState.Pending => "Pending",
                    MailSubmissionDeliveryState.DeliveredLocal => "DeliveredLocal",
                    MailSubmissionDeliveryState.PermanentFailure or MailSubmissionDeliveryState.Quarantined => "NotDelivered",
                    _ => "Unconfirmed",
                },
                ["Display"] = "Unreported",
            };
        return result;
    }

    private static JsonObject Reply(MailSubmissionDeliverySnapshot value) => value.State switch
    {
        MailSubmissionDeliveryState.Pending => new() { ["Kind"] = "Queued" },
        MailSubmissionDeliveryState.DeliveredLocal or MailSubmissionDeliveryState.DeliveredRemote => new() { ["Kind"] = "Accepted" },
        MailSubmissionDeliveryState.PermanentFailure => new() { ["Kind"] = "Failure", ["Detail"] = Sanitize(value.LastError ?? "Permanent delivery failure") },
        MailSubmissionDeliveryState.Quarantined => new() { ["Kind"] = "Quarantined", ["Detail"] = Sanitize(value.LastError ?? "Message quarantined") },
        _ => new() { ["Kind"] = "Unspecified" },
    };

    private static string Sanitize(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    private static MailSubmissionMutationFailure Failure(MailSubmissionMutationError error) => new(error, null, null, null, null, null);
}
