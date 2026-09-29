// A singleton domain snapshot and its internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailVacationReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool IncludeSingleton,
    [property: JsonRequired] bool IncludeBodies);

public enum MailVacationReadStatus
{
    Ok,
    AccountNotFound,
}

public sealed record MailVacationSnapshot(
    bool IsEnabled,
    DateTime? FromDate,
    DateTime? ToDate,
    string? Subject,
    string? TextBody,
    string? HtmlBody);

public sealed record MailVacationReadResult(
    MailVacationReadStatus Status,
    string? State,
    MailVacationSnapshot? Vacation);
