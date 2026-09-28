using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

// Text and property names preserve UTF-16 code units, including invalid scalar
// sequences. Transport never applies a consumer's presentation normalization.
public sealed record ApplicationValue(
    [property: JsonRequired] ApplicationValueKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadOnlyMemory<byte>? CodeUnits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Number = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Boolean = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ApplicationValueMember>? Members = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ApplicationValue>? Items = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);
