// Projected message results are an intermediate transport until MIME presentation moves to Gateway.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailMessageProjectionOptions(
    [property: JsonRequired] IReadOnlyList<string> Properties,
    [property: JsonRequired] IReadOnlyList<string> BodyProperties,
    [property: JsonRequired] bool FetchTextBodyValues,
    [property: JsonRequired] bool FetchHtmlBodyValues,
    [property: JsonRequired] bool FetchAllBodyValues,
    [property: JsonRequired] int MaxBodyValueBytes);

public sealed record MailMessageReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid>? MessageIds,
    [property: JsonRequired] MailMessageProjectionOptions Projection);

public enum MailMessageReadStatus { Ok, AccountNotFound, RequestTooLarge }

public sealed record MailMessageProjectedItem(
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] ApplicationValue Value);

public sealed record MailMessageReadResult(
    [property: JsonRequired] MailMessageReadStatus Status,
    [property: JsonRequired] string? State,
    [property: JsonRequired] IReadOnlyList<MailMessageProjectedItem> Messages);

public sealed record MailMessageParseCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<string> BlobIds,
    [property: JsonRequired] MailMessageProjectionOptions Projection);

public enum MailMessageParseStatus { Ok, AccountNotFound }

public enum MailMessageParseItemStatus { Parsed, NotFound, NotParsable }

public sealed record MailMessageParseItem(
    [property: JsonRequired] string BlobId,
    [property: JsonRequired] MailMessageParseItemStatus Status,
    [property: JsonRequired] ApplicationValue? Value);

public sealed record MailMessageParseResult(
    [property: JsonRequired] MailMessageParseStatus Status,
    [property: JsonRequired] IReadOnlyList<MailMessageParseItem> Items);
