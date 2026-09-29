// Ordered domain rows and their internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailThreadReadCommand(
    [property: JsonRequired] Guid AccountId);

public enum MailThreadReadStatus
{
    Ok,
    AccountNotFound,
}

public sealed record MailThreadEmailSnapshot(Guid EmailId, string? StoredThreadId);

public sealed record MailThreadReadResult(
    MailThreadReadStatus Status,
    string? State,
    IReadOnlyList<MailThreadEmailSnapshot> Emails);
