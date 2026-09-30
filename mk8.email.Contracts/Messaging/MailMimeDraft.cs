// One MIME draft and its part rows form a single protocol-neutral transport value.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailMimeDraft(
    [property: JsonRequired] IReadOnlyList<MailMimeHeaderSnapshot> Headers,
    [property: JsonRequired] IReadOnlyList<MailMimeDraftPart> Parts,
    [property: JsonRequired] int RootPart);

public sealed record MailMimeDraftPart(
    [property: JsonRequired] string MediaType,
    [property: JsonRequired] IReadOnlyList<MailMimeHeaderSnapshot> Headers,
    string? Text,
    string? BlobReference,
    [property: JsonRequired] IReadOnlyList<int> Children);

public sealed record MailMessageDraft(
    string? FolderReference,
    [property: JsonRequired] MailMessageMailboxIssue FolderIssue,
    [property: JsonRequired] IReadOnlyList<string> Keywords,
    [property: JsonRequired] MailMessageKeywordIssue KeywordIssue,
    DateTime? ReceivedAt,
    [property: JsonRequired] bool InvalidReceivedAt,
    MailMimeDraft? Mime,
    MailMessageMutationFailure? Failure,
    [property: JsonRequired] IReadOnlyList<string> BlobReferences,
    [property: JsonRequired] bool CheckBlobsBeforeFailure);
