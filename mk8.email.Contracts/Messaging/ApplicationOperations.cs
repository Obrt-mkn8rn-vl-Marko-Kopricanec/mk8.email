using mk8.email.Contracts.Enums;

namespace mk8.email.Contracts.Messaging;

public static class ApplicationOperations
{
    public const string SystemPing = "system.ping";
    public const string SmtpAuthenticatePassword = "smtp.authenticate-password";
    public const string SmtpAuthenticateOAuth = "smtp.authenticate-oauth";
    public const string SmtpCanSendAs = "smtp.can-send-as";
    public const string SmtpHasMatchingFromAddress = "smtp.has-matching-from-address";
    public const string SmtpCanReceive = "smtp.can-receive";
    public const string SmtpEnqueue = "smtp.enqueue";
    public const string SieveAuthenticatePassword = "sieve.authenticate-password";
    public const string SieveAuthenticateOAuth = "sieve.authenticate-oauth";
    public const string SieveCheckSpace = "sieve.check-space";
    public const string SieveList = "sieve.list";
    public const string SieveGet = "sieve.get";
    public const string SievePut = "sieve.put";
    public const string SieveSetActive = "sieve.set-active";
    public const string SieveDelete = "sieve.delete";
    public const string SieveRename = "sieve.rename";
    public const string SieveValidate = "sieve.validate";
    public const string Pop3AuthenticatePassword = "pop3.authenticate-password";
    public const string Pop3AuthenticateOAuth = "pop3.authenticate-oauth";
    public const string Pop3ListMaildrop = "pop3.maildrop.list";
    public const string Pop3GetMessage = "pop3.message.get";
    public const string Pop3CommitDeletes = "pop3.deletes.commit";
    public const string ImapAuthenticatePassword = "imap.authenticate-password";
    public const string ImapAuthenticateOAuth = "imap.authenticate-oauth";
    public const string ImapListMailboxes = "imap.mailboxes.list";
    public const string ImapGetMailboxStatuses = "imap.mailboxes.statuses";
    public const string ImapSetMailboxSubscription = "imap.mailboxes.subscription.set";
    public const string ImapCreateMailbox = "imap.mailboxes.create";
    public const string ImapRenameMailbox = "imap.mailboxes.rename";
    public const string ImapDeleteMailbox = "imap.mailboxes.delete";
    public const string ImapSelectMailbox = "imap.mailboxes.select";
    public const string ImapGetQuota = "imap.quota.get";
    public const string ImapGetIdleSnapshot = "imap.idle.snapshot";
    public const string ImapExpungeDeleted = "imap.messages.expunge";
    public const string ImapStoreFlags = "imap.messages.flags.store";
    public const string ImapMoveMessages = "imap.messages.move";
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
    public const string DavAuthenticate = "dav.authenticate";
    public const string DavEnsureCollections = "dav.collections.ensure";
    public const string DavCollectionsGet = "dav.collections.get";
    public const string DavCollectionGet = "dav.collection.get";
    public const string DavCollectionCreate = "dav.collection.create";
    public const string DavCollectionUpdate = "dav.collection.update";
    public const string DavCollectionDelete = "dav.collection.delete";
    public const string DavPrincipalsGet = "dav.principals.get";
    public const string DavPrincipalGet = "dav.principal.get";
    public const string DavSharesReplace = "dav.shares.replace";
    public const string DavResourcesGet = "dav.resources.get";
    public const string DavResourceGet = "dav.resource.get";
    public const string DavChangesGet = "dav.changes.get";
    public const string DavResourcePut = "dav.resource.put";
    public const string DavResourceDelete = "dav.resource.delete";
    public const string DavScheduleSubmit = "dav.schedule.submit";
}

public sealed record SystemPingResult(DateTimeOffset RespondedAt);

public sealed record AdminEnsureDomainRequest(string CompanyName, string Domain);

public sealed record AdminSetCatchAllRequest(string Domain, string TargetAddress);

public sealed record AdminSetDomainActiveRequest(string Domain, bool IsActive);

public sealed record AdminCreateAccountRequest(string Address, string Password, UserRole Role);

public sealed record AdminSetAccountActiveRequest(Guid UserId, bool IsActive);

public sealed record AdminResetPasswordRequest(Guid UserId, string Password);
