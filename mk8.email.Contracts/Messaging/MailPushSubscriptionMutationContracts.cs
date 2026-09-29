// Push mutation commands and outcomes form one internal transport contract.
#pragma warning disable MA0048
// These are wire-validated URI strings, not values normalized by System.Uri.
#pragma warning disable CA1054
#pragma warning disable CA1056
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailPushSubscriptionMutationCommand(
    [property: JsonRequired] IReadOnlyList<MailPushSubscriptionCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailPushSubscriptionUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailPushSubscriptionDestroy> Destroys);

public sealed record MailPushSubscriptionKeys(string P256dh, string Auth);

public sealed record MailPushSubscriptionCreateValues(
    string DeviceClientId,
    string Url,
    MailPushSubscriptionKeys? Keys,
    bool HasKeys,
    DateTimeOffset? Expires,
    bool HasExpires,
    IReadOnlyList<string>? Types,
    bool HasTypes);

public sealed record MailPushSubscriptionCreate(string CreationId, MailPushSubscriptionCreateValues? Values);

public sealed record MailPushSubscriptionTarget(Guid? ExistingId, string? CreatedKey);

public sealed record MailPushSubscriptionPatch(
    bool SetVerificationCode, string? VerificationCode,
    bool SetExpires, DateTimeOffset? Expires,
    bool SetTypes, IReadOnlyList<string>? Types,
    bool AssertId, string? Id,
    bool AssertDeviceClientId, string? DeviceClientId,
    bool AssertUrl, string? Url,
    bool AssertKeys, MailPushSubscriptionKeys? Keys,
    bool AssertKeyP256dh, string? KeyP256dh,
    bool AssertKeyAuth, string? KeyAuth,
    [property: JsonRequired] IReadOnlyList<string> UnknownProperties,
    bool HasUnsupportedKeysPath,
    bool ChangesUnsupportedKeys);

public sealed record MailPushSubscriptionUpdate(
    string RequestedId,
    [property: JsonRequired] MailPushSubscriptionTarget Target,
    MailPushSubscriptionPatch? Patch);

public sealed record MailPushSubscriptionDestroy(
    string RequestedId,
    [property: JsonRequired] MailPushSubscriptionTarget Target);

public enum MailPushSubscriptionMutationError
{
    None,
    Skipped,
    InvalidPatch,
    InvalidProperties,
    NotFound,
    OverQuota,
    RateLimit,
}

public sealed record MailPushSubscriptionCreateOutcome(
    string CreationId,
    Guid? SubscriptionId,
    MailPushSubscriptionMutationError Error,
    DateTime? ExpiresAt,
    MailPushSubscriptionKeys? Keys,
    IReadOnlyList<string>? Types);

public sealed record MailPushSubscriptionUpdateOutcome(
    string RequestedId,
    Guid? SubscriptionId,
    MailPushSubscriptionMutationError Error,
    DateTime? RevisedExpiresAt,
    IReadOnlyList<string>? InvalidProperties);

public sealed record MailPushSubscriptionDestroyOutcome(
    string RequestedId,
    Guid? SubscriptionId,
    MailPushSubscriptionMutationError Error);

public sealed record MailPushSubscriptionMutationResult(
    [property: JsonRequired] IReadOnlyList<MailPushSubscriptionCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailPushSubscriptionUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailPushSubscriptionDestroyOutcome> Destroyed);
