namespace mk8.email.Application.Tests;

internal sealed record AuthenticationSnapshot(
    AuthenticationPoint Point, long ElapsedMilliseconds, long Dropped,
    IReadOnlyList<AuthenticationEvent> Events, int ThreadPoolThreads, long PendingWorkItems, int AvailableWorkers);
