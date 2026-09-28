using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record ApplicationValueMember(
    [property: JsonRequired] ReadOnlyMemory<byte> NameCodeUnits,
    [property: JsonRequired] ApplicationValue Value);
