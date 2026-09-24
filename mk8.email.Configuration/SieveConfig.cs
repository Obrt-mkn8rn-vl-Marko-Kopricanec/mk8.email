namespace mk8.email.Configuration;

public sealed class SieveConfig
{
    public int Port { get; init; } = 4190;
    public bool EnableManageSieve { get; init; }
    public bool EnableStartTls { get; init; } = true;
    public int MaxScriptsPerUser { get; init; } = 64;
}
