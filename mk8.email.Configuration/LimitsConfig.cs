namespace mk8.email.Configuration;

public sealed class LimitsConfig
{
    public int MaxMessageSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxRecipientsPerMessage { get; init; } = 100;
    public int ConnectionTimeoutSeconds { get; init; } = 300;
    public int MaxConnectionsPerIp { get; init; } = 10;
}
