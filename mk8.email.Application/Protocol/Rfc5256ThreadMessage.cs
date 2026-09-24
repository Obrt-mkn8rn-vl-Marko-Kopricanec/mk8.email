namespace mk8.email.Application.Protocol;

internal sealed record Rfc5256ThreadMessage(
    int Identifier,
    int SequenceNumber,
    DateTime SentAt,
    string BaseSubjectKey,
    bool IsReplyOrForward,
    string? MessageId,
    IReadOnlyList<string> References);
