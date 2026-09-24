namespace mk8.email.Configuration;

public sealed class AdminConfig
{
    public IReadOnlyList<string> AllowedNetworks { get; init; } = [];
    public string DataProtectionKeyPath { get; init; } = "data-protection";
    public string AuditLogPath { get; init; } = "audit/admin.jsonl";
    public string HealthStatusPath { get; init; } = "health/status.json";
    public int SessionMinutes { get; init; } = 30;
}
