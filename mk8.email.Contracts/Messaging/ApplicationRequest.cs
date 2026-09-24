// The byte-array payload is part of the established JSON/public transport API.
#pragma warning disable CA1819
namespace mk8.email.Contracts.Messaging;

public sealed record ApplicationRequest(
    Guid Id,
    Guid SessionId,
    long Sequence,
    string Protocol,
    string Operation,
    string ContentType,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset Deadline,
    string? IdempotencyKey = null);
