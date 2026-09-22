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
    public const string OAuthPublicKeyGet = "oauth.public-key.get";
    public const string OAuthIdentityAuthenticate = "oauth.identity.authenticate";
    public const string OAuthAuthorize = "oauth.authorize";
    public const string OAuthAuthorizationCodeRedeem = "oauth.authorization-code.redeem";
    public const string OAuthTokenRefresh = "oauth.token.refresh";
    public const string OAuthTokenRevoke = "oauth.token.revoke";
    public const string JmapSessionGet = "jmap.session.get";
    public const string JmapApiProcess = "jmap.api.process";
    public const string JmapUpload = "jmap.upload";
    public const string JmapDownload = "jmap.download";
    public const string JmapEventPoll = "jmap.event.poll";
}

public sealed record SystemPingResult(DateTimeOffset RespondedAt);

public sealed record AdminEnsureDomainRequest(string CompanyName, string Domain);

public sealed record AdminSetCatchAllRequest(string Domain, string TargetAddress);

public sealed record AdminSetDomainActiveRequest(string Domain, bool IsActive);

public sealed record AdminCreateAccountRequest(string Address, string Password, UserRole Role);

public sealed record AdminSetAccountActiveRequest(Guid UserId, bool IsActive);

public sealed record AdminResetPasswordRequest(Guid UserId, string Password);
