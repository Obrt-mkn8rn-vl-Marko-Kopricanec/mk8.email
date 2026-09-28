using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

// Admission is based on requested domain features and operation volume, not
// on the API document or its presentation syntax.
public sealed record MailAdmissionPlan(
    [property: JsonRequired] IReadOnlyList<MailFeature> Features,
    [property: JsonPropertyName("invocationCount")] int OperationCount);
