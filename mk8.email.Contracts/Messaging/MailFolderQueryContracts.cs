// Folder query criteria and their internal transport are deliberately grouped.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailFolderFilterOperator
{
    Condition,
    And,
    Or,
    Not,
}

public enum MailFolderParentConstraint
{
    Any,
    Root,
    Folder,
    Impossible,
}

public sealed record MailFolderFilter(
    [property: JsonRequired] MailFolderFilterOperator Operator,
    [property: JsonRequired] IReadOnlyList<MailFolderFilter>? Conditions,
    [property: JsonRequired] MailFolderParentConstraint ParentConstraint,
    [property: JsonRequired] Guid? ParentId,
    [property: JsonRequired] string? Name,
    [property: JsonRequired] string? Role,
    [property: JsonRequired] bool MatchNullRole,
    [property: JsonRequired] bool? HasAnyRole,
    [property: JsonRequired] bool? IsSubscribed);

public enum MailFolderSortField
{
    SortOrder,
    Name,
}

public sealed record MailFolderSort(
    [property: JsonRequired] MailFolderSortField Field,
    [property: JsonRequired] bool IsAscending,
    [property: JsonRequired] MailStringCollation Collation);

public sealed record MailFolderQueryCriteria(
    [property: JsonRequired] MailFolderFilter? Filter,
    [property: JsonRequired] IReadOnlyList<MailFolderSort> Sort,
    [property: JsonRequired] bool SortAsTree,
    [property: JsonRequired] bool FilterAsTree);

public sealed record MailFolderQueryCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] MailFolderQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] long Position,
    [property: JsonRequired] Guid? AnchorId,
    [property: JsonRequired] bool AnchorCanMatch,
    [property: JsonRequired] long AnchorOffset,
    [property: JsonRequired] int Limit);

public sealed record MailFolderQueryChangesCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] MailFolderQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] string SinceState,
    [property: JsonRequired] long? MaxChanges);

public enum MailFolderQueryStatus
{
    Ok,
    AccountNotFound,
    Authorized,
    AnchorNotFound,
    CannotCalculateChanges,
    TooManyChanges,
}

public sealed record MailFolderQueryResult(
    MailFolderQueryStatus Status,
    string? State,
    long Position,
    [property: JsonRequired] IReadOnlyList<Guid> Ids,
    int Total);

public sealed record MailFolderIndexedId(Guid Id, int Index);

public sealed record MailFolderQueryChangesResult(
    MailFolderQueryStatus Status,
    string? State,
    [property: JsonRequired] IReadOnlyList<string> Removed,
    [property: JsonRequired] IReadOnlyList<MailFolderIndexedId> Added,
    int Total);
