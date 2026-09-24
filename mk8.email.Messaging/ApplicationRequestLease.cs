using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

public sealed record ApplicationRequestLease(
    ApplicationRequest Request,
    string WorkerId,
    DateTimeOffset LeaseExpiresAt,
    int AttemptCount);
