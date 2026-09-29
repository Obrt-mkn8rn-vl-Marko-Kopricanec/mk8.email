// Import command and outcome types intentionally share one transport-contract file.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailImportMailboxIssue
{
    None,
    Invalid,
    TooMany,
}

public enum MailImportKeywordIssue
{
    None,
    Invalid,
    TooMany,
}

public sealed record MailImportItem(
    [property: JsonRequired] string CreationId,
    [property: JsonRequired] string? BlobId,
    [property: JsonRequired] bool InvalidInitialProperties,
    [property: JsonRequired] Guid? MailboxId,
    [property: JsonRequired] MailImportMailboxIssue MailboxIssue,
    [property: JsonRequired] IReadOnlyList<string> Keywords,
    [property: JsonRequired] MailImportKeywordIssue KeywordIssue,
    [property: JsonRequired] DateTime? ReceivedAt,
    [property: JsonRequired] bool InvalidReceivedAt);

public sealed record MailImportCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] string? IfInState,
    [property: JsonRequired] IReadOnlyList<MailImportItem> Items);

public enum MailImportStatus
{
    Ok,
    AccountNotFound,
    StateMismatch,
    RequestTooLarge,
}

public enum MailImportItemError
{
    None,
    InvalidProperties,
    MissingBlob,
    InvalidMailbox,
    TooManyMailboxes,
    InvalidKeywords,
    TooManyKeywords,
    InvalidReceivedAt,
    TooLarge,
    OverQuota,
    InvalidEmail,
}

public sealed record MailImportItemOutcome(
    string CreationId,
    MailImportItemError Error,
    Guid? EmailId,
    string? StoredThreadId,
    long? Size);

public sealed record MailImportResult(
    MailImportStatus Status,
    string? OldState,
    string? NewState,
    IReadOnlyList<MailImportItemOutcome> Items);
