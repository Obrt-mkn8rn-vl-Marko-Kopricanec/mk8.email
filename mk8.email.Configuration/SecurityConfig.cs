namespace mk8.email.Configuration;

public sealed class SecurityConfig
{
    public bool EnableSpfCheck { get; init; }
    public bool EnableDmarcCheck { get; init; }
    public string PasswordHashScheme { get; init; } = "BLF-CRYPT";
}
