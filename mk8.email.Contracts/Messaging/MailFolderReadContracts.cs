// These transport shapes are deliberately grouped around one internal operation.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

// These are application data, not JMAP method arguments or response objects.
public sealed record MailFolderReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid>? FolderIds,
    [property: JsonRequired] bool CheckAccountOnly);

public enum MailFolderReadStatus
{
    Ok,
    AccountNotFound,
    RequestTooLarge,
}

public sealed record MailFolderSnapshot(
    Guid Id,
    string Name,
    Guid? ParentId,
    string? Role,
    long SortOrder,
    bool IsSubscribed,
    int TotalEmails,
    int UnreadEmails,
    int TotalThreads,
    int UnreadThreads,
    bool IsProtected);

public sealed record MailFolderReadResult(
    MailFolderReadStatus Status,
    string? State,
    IReadOnlyList<MailFolderSnapshot> Folders);

public sealed record MailFolderChangesCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] string SinceState,
    [property: JsonRequired] long? MaxChanges);

public enum MailFolderChangesStatus
{
    Ok,
    AccountNotFound,
    CannotCalculateChanges,
}

public sealed record MailFolderChangesResult(
    MailFolderChangesStatus Status,
    string? OldState,
    string? NewState,
    bool HasMoreChanges,
    IReadOnlyList<string> CreatedKeys,
    IReadOnlyList<string> UpdatedKeys,
    IReadOnlyList<string> DestroyedKeys);
