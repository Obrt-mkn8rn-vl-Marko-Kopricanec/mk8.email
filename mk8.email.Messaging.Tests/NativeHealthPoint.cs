namespace mk8.email.Messaging.Tests;

// Aggregate counts in this fixture's own database. No payloads, SQL/error text,
// credentials, leases, object names or cross-HTTP identity/causal timeline.
internal sealed record NativeHealthPoint(long Inbound, long Outbound, long Pending,
    long Processing, long Completed, long Failed, long Expired, long ResponseBlobs);
