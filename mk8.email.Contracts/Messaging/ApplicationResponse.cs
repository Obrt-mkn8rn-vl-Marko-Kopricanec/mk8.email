namespace mk8.email.Contracts.Messaging;

public sealed record ApplicationResponse(
    Guid RequestId,
    string ContentType,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Metadata,
    bool IsError = false,
    string? ErrorCode = null,
    string? ErrorDetail = null);
