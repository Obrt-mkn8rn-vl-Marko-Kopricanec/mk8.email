namespace mk8.email.Application.Worker;

internal sealed record ApplicationWorkerIdentity(
    string WorkerId,
    TimeSpan LeaseRenewalInterval);
