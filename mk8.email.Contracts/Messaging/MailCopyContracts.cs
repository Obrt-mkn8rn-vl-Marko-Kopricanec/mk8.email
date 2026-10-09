// Cross-account message copying and optional source deletion share one atomic result.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailCopyItem(
    [property: JsonRequired] string CreationId,
    [property: JsonRequired] Guid? SourceEmailId,
    [property: JsonRequired] bool InvalidInitialProperties,
    [property: JsonRequired] Guid? MailboxId,
    [property: JsonRequired] MailMessageMailboxIssue MailboxIssue,
    [property: JsonRequired] IReadOnlyList<string>? Keywords,
    [property: JsonRequired] MailMessageKeywordIssue KeywordIssue,
    [property: JsonRequired] DateTime? ReceivedAt,
    [property: JsonRequired] bool InvalidReceivedAt);

public sealed record MailCopyCommand(
    [property: JsonRequired] Guid SourceAccountId,
    [property: JsonRequired] Guid TargetAccountId,
    [property: JsonRequired] string? IfFromInState,
    [property: JsonRequired] string? IfInState,
    [property: JsonRequired] bool DestroyOriginal,
    [property: JsonRequired] string? DestroyFromIfInState,
    [property: JsonRequired] IReadOnlyList<MailCopyItem> Items)
{
    [JsonRequired]
    public string? IfMailboxInState { get; init; }
}

public enum MailCopyStatus
{
    Ok,
    SourceAccountNotFound,
    TargetAccountNotFound,
    StateMismatch,
    RequestTooLarge,
}

public enum MailCopyItemError
{
    None,
    InvalidProperties,
    NotFound,
    InvalidMailbox,
    TooManyMailboxes,
    InvalidKeywords,
    TooManyKeywords,
    InvalidReceivedAt,
    TooLarge,
    OverQuota,
    InvalidEmail,
}

public sealed record MailCopyItemOutcome(
    string CreationId,
    MailCopyItemError Error,
    Guid? EmailId,
    string? StoredThreadId,
    long? Size);

public enum MailCopyDestroyStatus
{
    NotAttempted,
    StateMismatch,
    Completed,
}

public sealed record MailCopyDestroyOutcome(
    MailCopyDestroyStatus Status,
    string? OldState,
    string? NewState,
    IReadOnlyList<Guid> Destroyed,
    IReadOnlyList<Guid> NotFound);

public sealed record MailCopyResult(
    MailCopyStatus Status,
    string? OldTargetState,
    string? NewTargetState,
    IReadOnlyList<MailCopyItemOutcome> Items,
    MailCopyDestroyOutcome? Destroy);
