// One protocol-neutral MIME snapshot graph is frozen in an encrypted operation receipt.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailMimeHeaderSnapshot(
    [property: JsonRequired] ReadOnlyMemory<byte> RawField,
    [property: JsonRequired] ReadOnlyMemory<byte> RawValue);

public sealed record MailMimePartSnapshot(
    [property: JsonRequired] string? Path,
    [property: JsonRequired] long DecodedSize,
    [property: JsonRequired] IReadOnlyList<MailMimeHeaderSnapshot> Headers,
    string? Name,
    [property: JsonRequired] string MediaType,
    string? Charset,
    string? Disposition,
    string? ContentId,
    IReadOnlyList<string>? Languages,
    string? Location,
    string? Text,
    [property: JsonRequired] bool EncodingProblem,
    [property: JsonRequired] IReadOnlyList<int> Children);

public sealed record MailStoredMessageSnapshot(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] Guid FolderId,
    [property: JsonRequired] string ThreadKey,
    [property: JsonRequired] IReadOnlyList<string> Keywords,
    [property: JsonRequired] long Size,
    [property: JsonRequired] DateTime ReceivedAt);

public sealed record MailMessageSnapshot(
    [property: JsonRequired] Guid ContentSourceId,
    string? UploadedContentId,
    string? PartPrefix,
    [property: JsonRequired] long RawSize,
    MailStoredMessageSnapshot? Stored,
    [property: JsonRequired] IReadOnlyList<MailMimeHeaderSnapshot> Headers,
    [property: JsonRequired] int? RootPart,
    [property: JsonRequired] IReadOnlyList<MailMimePartSnapshot> Parts);
