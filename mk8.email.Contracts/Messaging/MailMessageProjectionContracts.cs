// Message reads return MIME data; selection and protocol response shaping stay in Gateway.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailMessageReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid>? MessageIds,
    [property: JsonRequired] bool IncludeText);

public enum MailMessageReadStatus { Ok, AccountNotFound, RequestTooLarge }

public sealed record MailMessageProjectedItem(
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] MailMessageSnapshot Value);

public sealed record MailMessageReadResult(
    [property: JsonRequired] MailMessageReadStatus Status,
    [property: JsonRequired] string? State,
    [property: JsonRequired] IReadOnlyList<MailMessageProjectedItem> Messages);

public sealed record MailMessageParseCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<string> BlobIds,
    [property: JsonRequired] bool IncludeText);

public enum MailMessageParseStatus { Ok, AccountNotFound }

public enum MailMessageParseItemStatus { Parsed, NotFound, NotParsable }

public sealed record MailMessageParseItem(
    [property: JsonRequired] string BlobId,
    [property: JsonRequired] MailMessageParseItemStatus Status,
    [property: JsonRequired] MailMessageSnapshot? Value);

public sealed record MailMessageParseResult(
    [property: JsonRequired] MailMessageParseStatus Status,
    [property: JsonRequired] IReadOnlyList<MailMessageParseItem> Items);
