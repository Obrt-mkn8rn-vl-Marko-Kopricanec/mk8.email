using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailOperationResponse(
    [property: JsonRequired] MailOperationKind Operation,
    [property: JsonRequired] ApplicationValue Data,
    IReadOnlyList<MailOperationResponse>? AdditionalResults = null);
