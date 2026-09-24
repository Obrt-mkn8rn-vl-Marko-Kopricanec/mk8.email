namespace mk8.email.Configuration;

public sealed class QueueConfig
{
    public int PollIntervalMilliseconds { get; init; } = 500;
    public int LeaseSeconds { get; init; } = 300;
    public int MaxAttempts { get; init; } = 20;
    public int MaxAgeHours { get; init; } = 120;
    public int CompletedRetentionDays { get; init; } = 14;
}
