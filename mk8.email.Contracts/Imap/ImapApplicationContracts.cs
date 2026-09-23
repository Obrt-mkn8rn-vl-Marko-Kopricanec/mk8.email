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

    Task<ImapQuotaResult> GetQuotaAsync(
        ImapQuotaRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
        ImapIdleSnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapExpungeResult> ExpungeDeletedAsync(
        ImapExpungeRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapStoreResult> StoreFlagsAsync(
        ImapStoreRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapMoveResult> MoveMessagesAsync(
        ImapMoveRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapCopyResult> CopyMessagesAsync(
        ImapCopyRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapAppendPreflightResult> CheckAppendCapacityAsync(
        ImapAppendPreflightRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapAppendResult> AppendMessagesAsync(
        ImapAppendRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapSearchResult> SearchMessagesAsync(
        ImapSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapSortResult> SortMessagesAsync(
        ImapSortRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapThreadResult> ThreadMessagesAsync(
        ImapThreadRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapMarkSeenResult> MarkMessagesSeenAsync(
        ImapMarkSeenRequest request,
        CancellationToken cancellationToken = default);

    Task<ImapFetchPageResult> GetFetchPageAsync(
        ImapFetchPageRequest request,
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

public sealed record ImapQuotaRequest(Guid UserId, string? MailboxName);

public sealed record ImapQuotaResult(
    bool MailboxFound,
    long UsedBytes,
    long LimitBytes);

public sealed record ImapIdleSnapshotRequest(Guid UserId, Guid FolderId);

public sealed record ImapIdleSnapshotResult(
    bool FolderFound,
    long HighestModSeq,
    List<ImapIdleMessage> Messages);

public sealed record ImapIdleMessage(
    Guid Id,
    int Uid,
    long ModSeq,
    bool IsRead,
    bool IsDeleted,
    bool IsFlagged,
    bool IsDraft,
    bool IsAnswered,
    string[] Keywords);

public sealed record ImapExpungeRequest(
    Guid UserId,
    Guid FolderId,
    ImapUidSelection? UidSelection = null);

public sealed record ImapUidSelection(
    List<ImapUidRange>? Ranges,
    List<int>? SavedSearchUids);

public sealed record ImapUidRange(int? Start, int? End);

public sealed record ImapExpungeResult(
    bool FolderFound,
    List<ImapExpungedMessage> Messages);

public sealed record ImapExpungedMessage(int SequenceNumber, int Uid);

public sealed record ImapStoreRequest(
    Guid UserId,
    Guid FolderId,
    bool UseUid,
    ImapMessageSelection Selection,
    long? UnchangedSince,
    ImapFlagMutationMode Mode,
    string[] Flags);

public sealed record ImapMessageSelection(
    List<ImapMessageRange>? Ranges,
    List<int>? SavedSearchUids);

public sealed record ImapMessageRange(int? Start, int? End);

public enum ImapFlagMutationMode
{
    Replace,
    Add,
    Remove,
}

public enum ImapStoreDisposition
{
    Stored,
    FolderNotFound,
    KeywordLimitExceeded,
}

public sealed record ImapStoreResult(
    ImapStoreDisposition Disposition,
    List<int> Modified,
    List<ImapChangedMessage> Updated);

public sealed record ImapMoveRequest(
    Guid UserId,
    Guid SourceFolderId,
    string DestinationMailboxName,
    bool UseUid,
    ImapMessageSelection Selection);

public enum ImapMoveDisposition
{
    Moved,
    SourceNotFound,
    DestinationNotFound,
}

public sealed record ImapMoveResult(
    ImapMoveDisposition Disposition,
    int DestinationUidValidity,
    List<int> SourceUids,
    List<int> DestinationUids,
    List<int> ExpungeSequenceNumbers);

public sealed record ImapCopyRequest(
    Guid UserId,
    Guid SourceFolderId,
    string DestinationMailboxName,
    bool UseUid,
    ImapMessageSelection Selection);

public enum ImapCopyDisposition
{
    Copied,
    SourceNotFound,
    DestinationNotFound,
    InvalidSourceSize,
    OverQuota,
}

public sealed record ImapCopyResult(
    ImapCopyDisposition Disposition,
    int DestinationUidValidity,
    List<int> SourceUids,
    List<int> DestinationUids);

public sealed record ImapAppendPreflightRequest(
    Guid UserId,
    string MailboxName,
    long AddedBytes);

public enum ImapAppendPreflightDisposition
{
    Ready,
    MailboxNotFound,
    OverQuota,
}

public sealed record ImapAppendPreflightResult(ImapAppendPreflightDisposition Disposition);

public sealed record ImapAppendRequest(
    Guid UserId,
    string MailboxName,
    bool Utf8Enabled,
    List<ImapAppendMessage> Messages);

public sealed record ImapAppendMessage(
    Guid MessageId,
    string[] Flags,
    DateTime? InternalDate,
    byte[] RawMessage);

public enum ImapAppendDisposition
{
    Appended,
    MailboxNotFound,
    OverQuota,
    InvalidFlags,
    InvalidContent,
}

public sealed record ImapAppendResult(
    ImapAppendDisposition Disposition,
    int UidValidity,
    List<int> Uids);

public sealed record ImapSearchRequest(
    Guid UserId,
    Guid FolderId,
    string Criteria,
    List<int> SavedSearchUids,
    bool Utf8Enabled);

public sealed record ImapSearchResult(
    bool FolderFound,
    string? FailureResponse,
    List<ImapSearchMatch> Matches,
    long? HighestModSequence);

public sealed record ImapSearchMatch(int Uid, int SequenceNumber);

public enum ImapSortKey
{
    Arrival,
    Cc,
    Date,
    From,
    Size,
    Subject,
    To,
}

public sealed record ImapSortCriterion(ImapSortKey Key, bool Reverse);

public sealed record ImapSortRequest(
    Guid UserId,
    Guid FolderId,
    string SearchCriteria,
    List<int> SavedSearchUids,
    bool Utf8Enabled,
    string Charset,
    List<ImapSortCriterion> SortCriteria);

public sealed record ImapSortResult(
    bool FolderFound,
    string? FailureResponse,
    List<ImapSearchMatch> SortedMatches,
    long? HighestModSequence);

public enum ImapThreadAlgorithm
{
    References,
    OrderedSubject,
}

public sealed record ImapThreadRequest(
    Guid UserId,
    Guid FolderId,
    string SearchCriteria,
    List<int> SavedSearchUids,
    bool Utf8Enabled,
    string Charset,
    ImapThreadAlgorithm Algorithm,
    bool UseUid);

// Nodes are in preorder. A parent is either -1 (a root) or an earlier index.
// Null identifiers represent RFC 5256 dummy containers, not message rows.
public sealed record ImapThreadNode(int? Identifier, int ParentIndex);

public sealed record ImapThreadResult(
    bool FolderFound,
    string? FailureResponse,
    List<ImapThreadNode> Nodes);

public sealed record ImapMarkSeenRequest(
    Guid UserId,
    Guid FolderId,
    List<Guid> MessageIds);

public sealed record ImapMarkSeenResult(
    bool FolderFound,
    List<ImapSeenMessage> Messages);

public sealed record ImapSeenMessage(Guid Id, bool Found, long ModSeq);

public sealed record ImapFetchPageRequest(
    Guid UserId,
    Guid FolderId,
    bool UseUid,
    ImapMessageSelection Selection,
    int AfterUid,
    int? SnapshotMaxUid,
    int? SnapshotMaximumIdentifier,
    bool IncludeStoredContent);

public sealed record ImapFetchPageResult(
    bool FolderFound,
    int SnapshotMaxUid,
    int SnapshotMaximumIdentifier,
    int NextAfterUid,
    bool HasMore,
    List<ImapFetchMessage> Messages);

public sealed record ImapFetchMessage(
    Guid Id,
    int SequenceNumber,
    int Uid,
    long ModSeq,
    bool IsRead,
    bool IsDeleted,
    bool IsFlagged,
    bool IsDraft,
    bool IsAnswered,
    string[] Keywords,
    DateTime ReceivedAt,
    int SizeBytes,
    string Sender,
    string Recipient,
    string? Cc,
    string Subject,
    string Body,
    string? RawHeaders,
    string? MessageId,
    string? InReplyTo,
    string? EmailObjectId,
    string? ThreadObjectId,
    byte[]? RawMessage);
