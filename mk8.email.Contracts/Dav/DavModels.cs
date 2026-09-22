namespace mk8.email.Dav;

// Shared, transport-safe DAV values. HTTP handling belongs to Gateway; persistence and
// scheduling belong to the Application Worker.
public enum DavCollectionKind
{
    Calendar,
    AddressBook,
}

public enum DavCollectionAccess
{
    Owner,
    ReadOnly,
    ReadWrite,
}

public sealed record DavShareGrant(Guid UserId, DavCollectionAccess Access);

public sealed record DavPrincipal(Guid Id, string Username);

public sealed record DavCollection(
    Guid Id,
    Guid UserId,
    DavCollectionAccess Access,
    Guid HrefUserId,
    string HrefSlug,
    IReadOnlyList<DavShareGrant> Shares,
    DavCollectionKind Kind,
    string Slug,
    string DisplayName,
    string? Description,
    string? Color,
    int SortOrder,
    string[] Components,
    long SyncToken,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public bool IsOwner => Access == DavCollectionAccess.Owner;
    public bool CanWrite => Access is DavCollectionAccess.Owner or DavCollectionAccess.ReadWrite;
}

public sealed record DavResource(
    Guid Id,
    Guid CollectionId,
    string ResourceName,
    string Uid,
    string ContentType,
    byte[] Content,
    string Etag,
    int SizeBytes,
    long ChangeSequence,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DavChange(
    long Sequence,
    string ResourceName,
    bool IsDeleted,
    string? Etag,
    DateTime ChangedAt);

public sealed record DavCollectionProperties(
    string DisplayName,
    string? Description,
    string? Color,
    int SortOrder,
    string[] Components);

public enum DavCollectionWriteStatus
{
    Created,
    Updated,
    NotFound,
    AlreadyExists,
    LimitExceeded,
    Protected,
    Forbidden,
}

public sealed record DavCollectionWriteResult(
    DavCollectionWriteStatus Status,
    DavCollection? Collection = null);

public enum DavAclWriteStatus
{
    Updated,
    NotFound,
    Forbidden,
    Protected,
    TooManyEntries,
    UnrecognizedPrincipal,
    DisallowedPrincipal,
    BindingConflict,
}

public sealed record DavAclWriteResult(DavAclWriteStatus Status);

public enum DavResourceWriteStatus
{
    Created,
    Updated,
    Unchanged,
    NotFound,
    PreconditionFailed,
    UidConflict,
    LimitExceeded,
    Forbidden,
}

public sealed record DavResourceWriteResult(
    DavResourceWriteStatus Status,
    DavResource? Resource = null);

public sealed record DavContentInfo(
    string Uid,
    string ContentType,
    HashSet<string> Components,
    string Text,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Properties);

public sealed record DavScheduleRecipientResult(
    string Recipient,
    string RequestStatus,
    string? CalendarData = null);

public sealed record DavScheduleSubmissionResult(
    int StatusCode,
    string? Error,
    IReadOnlyList<DavScheduleRecipientResult> Recipients);
