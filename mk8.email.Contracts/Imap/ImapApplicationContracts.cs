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

    Task<ImapMailboxCreateResult> CreateMailboxAsync(
        ImapMailboxCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapMailboxRenameResult> RenameMailboxAsync(
        ImapMailboxRenameRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
        ImapMailboxDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapMailboxSelectResult> SelectMailboxAsync(
        ImapMailboxSelectRequest request,
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

public sealed record ImapMailboxCreateRequest(Guid UserId, string MailboxName);

public enum ImapMailboxCreateDisposition
{
    Created,
    InvalidName,
    AlreadyExists,
}

public sealed record ImapMailboxCreateResult(
    ImapMailboxCreateDisposition Disposition,
    Guid FolderId,
    string? MailboxId);

public sealed record ImapMailboxRenameRequest(
    Guid UserId,
    string OldName,
    string NewName);

public enum ImapMailboxRenameDisposition
{
    Renamed,
    NotFound,
    SystemFolder,
    InvalidDestination,
    AlreadyExists,
}

public sealed record ImapMailboxRenameResult(ImapMailboxRenameDisposition Disposition);

public sealed record ImapMailboxDeleteRequest(Guid UserId, string MailboxName);

public enum ImapMailboxDeleteDisposition
{
    Deleted,
    NotFound,
    SystemFolder,
}

public sealed record ImapMailboxDeleteResult(
    ImapMailboxDeleteDisposition Disposition,
    Guid FolderId);

public sealed record ImapMailboxSelectRequest(
    Guid UserId,
    string MailboxName,
    int? QresyncUidValidity,
    long? QresyncModSeq);

public sealed record ImapMailboxSelectResult(ImapSelectedMailbox? Mailbox);

public sealed record ImapSelectedMailbox(
    Guid FolderId,
    int UidValidity,
    int NextUid,
    long HighestModSeq,
    string MailboxId,
    int MessageCount,
    int? FirstUnseenSequence,
    List<string> Keywords,
    List<int> VanishedUids,
    List<ImapChangedMessage> ChangedMessages);

public sealed record ImapChangedMessage(
    int Sequence,
    int Uid,
    long ModSeq,
    bool IsRead,
    bool IsDeleted,
    bool IsFlagged,
    bool IsDraft,
    bool IsAnswered,
    string[] Keywords);
