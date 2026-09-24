namespace mk8.email.Configuration;

public sealed class MfaConfig
{
    public bool EnableTotp { get; init; }
    public string Issuer { get; init; } = "mk8.email";
    public string EncryptionKey { get; set; } = string.Empty;
    public string? EncryptionKeyFile { get; init; }
    public int RecoveryCodeCount { get; init; } = 10;
}
