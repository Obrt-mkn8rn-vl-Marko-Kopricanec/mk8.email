namespace mk8.email.Configuration;

public sealed class AdminConfig
{
    public IReadOnlyList<string> AllowedNetworks { get; init; } = [];
    // Retained only to refuse the old filesystem-keyring setting explicitly.
    public string DataProtectionKeyPath { get; init; } = string.Empty;
    public IReadOnlyList<DataProtectionKeyConfig> DataProtectionKeys { get; init; } = [];
    public string AuditLogPath { get; init; } = "audit/admin.jsonl";
    public string HealthStatusPath { get; init; } = "health/status.json";
    public int SessionMinutes { get; init; } = 30;
}
