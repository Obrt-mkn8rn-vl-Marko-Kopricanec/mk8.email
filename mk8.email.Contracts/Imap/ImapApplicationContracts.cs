namespace mk8.email.Contracts.Imap;

public interface IImapApplicationService
{
    Task<ImapIdentityResult> AuthenticatePasswordAsync(
        ImapPasswordAuthentication request,
        CancellationToken cancellationToken = default);

    Task<ImapIdentityResult> AuthenticateOAuthAsync(
        ImapOAuthAuthentication request,
        CancellationToken cancellationToken = default);

    Task<ImapMailboxListResult> ListMailboxesAsync(
        ImapMailboxListRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ImapPasswordAuthentication(string Username, string Password);

public sealed record ImapOAuthAuthentication(string Username, string AccessToken);

public sealed record ImapIdentityResult(Guid? UserId, string? Username);

public sealed record ImapMailboxListRequest(Guid UserId, bool SubscribedOnly);

public sealed record ImapMailboxInfo(
    string InboxName,
    string Domain,
    string FolderName,
    bool IsPrimary,
    bool IsSubscribed);

public sealed record ImapMailboxListResult(List<ImapMailboxInfo> Mailboxes);
