using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailOperationFailure(
    [property: JsonRequired] MailOperationFailureReason Reason,
    string? Explanation = null);
