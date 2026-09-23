namespace mk8.email.Wake;

internal sealed record WorkerWakeSnapshot(bool HasDueWork, DateTimeOffset? NextDueAt);
