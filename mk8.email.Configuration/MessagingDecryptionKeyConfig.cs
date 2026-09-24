namespace mk8.email.Configuration;

public sealed class MessagingDecryptionKeyConfig
{
    public string Id { get; init; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? KeyFile { get; init; }
}
