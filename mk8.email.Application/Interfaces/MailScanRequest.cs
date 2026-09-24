namespace mk8.email.Application.Interfaces;

public sealed record MailScanRequest(
    Guid QueueId,
    string EnvelopeSender,
    IReadOnlyList<string> Recipients,
    string RawMessage,
    string? ClientIp,
    string? Helo,
    string? AuthenticatedUser);
