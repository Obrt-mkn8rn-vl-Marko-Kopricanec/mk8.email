namespace mk8.email.Messaging;

public sealed class PostgresMessagingOptions
{
    public int MaxPayloadBytes { get; init; } = 64 * 1024 * 1024;
    public int InlinePayloadThresholdBytes { get; init; } = 256 * 1024;
    public int MaxMetadataBytes { get; init; } = 64 * 1024;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan NotificationFallbackInterval { get; init; } = TimeSpan.FromSeconds(30);

    // These names identify invalid configuration properties, not method parameters.
#pragma warning disable MA0015
    internal void Validate()
    {
        if (MaxPayloadBytes is < 65_536 or > 1_073_741_824)
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        if (InlinePayloadThresholdBytes is < 0 or > 1_048_576
            || InlinePayloadThresholdBytes > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(InlinePayloadThresholdBytes));
        }
        if (MaxMetadataBytes is < 1_024 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(MaxMetadataBytes));
        if (LeaseDuration < TimeSpan.FromSeconds(5) || LeaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (NotificationFallbackInterval < TimeSpan.FromMilliseconds(100)
            || NotificationFallbackInterval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(NotificationFallbackInterval));
        }
    }
#pragma warning restore MA0015
}
