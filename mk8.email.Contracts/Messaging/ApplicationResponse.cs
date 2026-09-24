// The byte-array payload is part of the established JSON/public transport API.
#pragma warning disable CA1819
namespace mk8.email.Contracts.Messaging;

public sealed record ApplicationResponse(
    Guid RequestId,
    string ContentType,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Metadata,
    bool IsError = false,
    string? ErrorCode = null,
    string? ErrorDetail = null);
