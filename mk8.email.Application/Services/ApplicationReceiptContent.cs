using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Services;

public sealed record ApplicationReceiptContent(ReadOnlyMemory<byte> Result, IReadOnlyList<ApplicationRequest> Effects);
