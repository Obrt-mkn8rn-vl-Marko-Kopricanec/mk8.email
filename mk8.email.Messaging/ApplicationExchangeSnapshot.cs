using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

public sealed record ApplicationExchangeSnapshot(
    ApplicationRequest Request,
    string State,
    int AttemptCount,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    ApplicationResponse? Response,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureDetail);
