namespace mk8.email.Configuration;

public sealed class MessagingConfig
{
    public bool Enabled { get; init; }
    public string EncryptionKeyId { get; init; } = "primary";
    public string EncryptionKey { get; set; } = string.Empty;
    public string? EncryptionKeyFile { get; init; }
    public IReadOnlyList<MessagingDecryptionKeyConfig> DecryptionKeys { get; init; } = [];
    public int MaxPayloadBytes { get; init; } = 64 * 1024 * 1024;
    public int InlinePayloadThresholdBytes { get; init; } = 256 * 1024;
    public int LeaseSeconds { get; init; } = 120;
    public int NotificationFallbackSeconds { get; init; } = 30;
    public string? WorkerId { get; init; }
}
