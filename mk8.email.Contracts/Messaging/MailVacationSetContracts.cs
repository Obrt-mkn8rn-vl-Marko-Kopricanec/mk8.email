// Vacation-response domain mutations and their internal transport are deliberately grouped.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailVacationSetCommand(
    [property: JsonRequired] Guid AccountId,
    string? IfInState,
    [property: JsonRequired] IReadOnlyList<MailVacationUpdate> Updates);

public sealed record MailVacationUpdate(
    bool SetIsEnabled,
    bool IsEnabled,
    bool SetFromDate,
    DateTime? FromDate,
    bool SetToDate,
    DateTime? ToDate,
    bool SetSubject,
    string? Subject,
    bool SetTextBody,
    string? TextBody,
    bool SetHtmlBody,
    string? HtmlBody);

public enum MailVacationSetStatus
{
    Ok,
    AccountNotFound,
    StateMismatch,
}

public sealed record MailVacationUpdateResult(
    bool Updated,
    IReadOnlyList<string> InvalidProperties);

public sealed record MailVacationSetResult(
    MailVacationSetStatus Status,
    string? OldState,
    string? NewState,
    IReadOnlyList<MailVacationUpdateResult> Updates);
