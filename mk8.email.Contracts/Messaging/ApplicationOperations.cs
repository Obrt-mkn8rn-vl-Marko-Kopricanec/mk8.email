using mk8.email.Contracts.Enums;

namespace mk8.email.Contracts.Messaging;

public static class ApplicationOperations
{
    public const string SystemPing = "system.ping";
    public const string AdminAuthenticate = "admin.authenticate";
    public const string AdminDashboardGet = "admin.dashboard.get";
    public const string AdminDomainsGet = "admin.domains.get";
    public const string AdminDomainsEnsure = "admin.domains.ensure";
    public const string AdminDomainsSetCatchAll = "admin.domains.set-catch-all";
    public const string AdminDomainsSetActive = "admin.domains.set-active";
    public const string AdminAccountsGet = "admin.accounts.get";
    public const string AdminAccountsCreate = "admin.accounts.create";
    public const string AdminAccountsSetActive = "admin.accounts.set-active";
    public const string AdminAccountsResetPassword = "admin.accounts.reset-password";
}

public sealed record SystemPingResult(DateTimeOffset RespondedAt);

public sealed record AdminEnsureDomainRequest(string CompanyName, string Domain);

public sealed record AdminSetCatchAllRequest(string Domain, string TargetAddress);

public sealed record AdminSetDomainActiveRequest(string Domain, bool IsActive);

public sealed record AdminCreateAccountRequest(string Address, string Password, UserRole Role);

public sealed record AdminSetAccountActiveRequest(Guid UserId, bool IsActive);

public sealed record AdminResetPasswordRequest(Guid UserId, string Password);
