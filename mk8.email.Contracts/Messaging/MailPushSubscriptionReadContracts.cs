// Domain snapshots and their internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailPushSubscriptionReadCommand(
    [property: JsonRequired] IReadOnlyList<Guid>? SubscriptionIds);

public enum MailPushSubscriptionReadStatus
{
    Ok,
    RequestTooLarge,
}

public sealed record MailPushSubscriptionSnapshot(
    Guid Id,
    string DeviceClientId,
    string? VerificationCode,
    DateTime ExpiresAt,
    IReadOnlyList<string>? Types);

public sealed record MailPushSubscriptionReadResult(
    MailPushSubscriptionReadStatus Status,
    IReadOnlyList<MailPushSubscriptionSnapshot> Subscriptions);
