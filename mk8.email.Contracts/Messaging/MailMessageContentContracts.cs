// Protocol-neutral, bounded native message content; formatting remains in Gateway.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailMessageContentCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid> MessageIds,
    [property: JsonRequired] bool IncludeText,
    [property: JsonRequired] int MaximumBytes);

public enum MailMessageContentStatus { Ok, NotFound, TooLarge, NotParsable }

public sealed record MailMessageContentItem(
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] MailMessageContentStatus Status,
    [property: JsonRequired] MailMessageSnapshot? Value,
    [property: JsonRequired] ReadOnlyMemory<byte> Content);

public sealed record MailMessageContentResult(
    [property: JsonRequired] MailMessageReadStatus Status,
    [property: JsonRequired] string? State,
    [property: JsonRequired] IReadOnlyList<MailMessageContentItem> Messages);
