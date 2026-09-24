namespace mk8.email.Gateway.ApplicationBridge;

public sealed record GatewayApplicationOptions(
    string InstanceId,
    TimeSpan RequestTimeout,
    TimeSpan TrafficJournalTimeout);
