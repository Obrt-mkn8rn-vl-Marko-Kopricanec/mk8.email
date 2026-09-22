using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

public static class ApplicationExchangeStates
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public sealed record ApplicationRequestLease(
    ApplicationRequest Request,
    string WorkerId,
    DateTimeOffset LeaseExpiresAt,
    int AttemptCount);

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
