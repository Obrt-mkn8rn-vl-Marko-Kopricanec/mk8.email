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

    Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
        ImapMailboxStatusRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
        ImapMailboxSubscriptionRequest request,
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

public sealed record ImapMailboxStatusRequest(
    Guid UserId,
    List<string> MailboxNames,
    bool IncludeMessageCount,
    bool IncludeUnseenCount,
    bool IncludeSize);

public sealed record ImapMailboxStatus(
    Guid FolderId,
    int UidValidity,
    int NextUid,
    long HighestModSeq,
    string MailboxId,
    int? MessageCount,
    int? UnseenCount,
    long? SizeBytes);

public sealed record ImapMailboxStatusResult(Dictionary<string, ImapMailboxStatus> Statuses);

public sealed record ImapMailboxSubscriptionRequest(
    Guid UserId,
    string MailboxName,
    bool IsSubscribed);

public sealed record ImapMailboxSubscriptionResult(bool Found);
