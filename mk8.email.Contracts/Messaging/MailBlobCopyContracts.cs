// Binary-object copy outcomes and their internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailBlobCopyCommand(
    [property: JsonRequired] Guid FromAccountId,
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<string> BlobIds);

public enum MailBlobCopyStatus
{
    Ok,
    FromAccountNotFound,
    AccountNotFound,
    RequestTooLarge,
}

public enum MailBlobCopyItemStatus
{
    Copied,
    NotFound,
    TooLarge,
}

public sealed record MailBlobCopyItemResult(
    string BlobId,
    MailBlobCopyItemStatus Status,
    string? CopiedBlobId);

public sealed record MailBlobCopyResult(
    MailBlobCopyStatus Status,
    IReadOnlyList<MailBlobCopyItemResult> Items);
