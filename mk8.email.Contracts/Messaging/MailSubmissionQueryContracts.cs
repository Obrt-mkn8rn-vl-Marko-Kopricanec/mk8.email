// Submission query criteria and their internal transport are deliberately grouped.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailSubmissionFilterOperator
{
    Condition,
    And,
    Or,
    Not,
}

public sealed record MailSubmissionFilter(
    MailSubmissionFilterOperator Operator,
    IReadOnlyList<MailSubmissionFilter>? Conditions,
    IReadOnlyList<string>? IdentityIds,
    IReadOnlyList<string>? EmailIds,
    IReadOnlyList<string>? ThreadIds,
    string? UndoStatus,
    DateTime? Before,
    DateTime? After);

public enum MailSubmissionSortField
{
    SentAt,
    EmailId,
    ThreadId,
}

public enum MailStringCollation
{
    UnicodeCasemap,
    AsciiNumeric,
    AsciiCasemap,
}

public sealed record MailSubmissionSort(
    MailSubmissionSortField Field,
    bool IsAscending,
    MailStringCollation Collation);

public sealed record MailSubmissionQueryCriteria(
    MailSubmissionFilter? Filter,
    [property: JsonRequired] IReadOnlyList<MailSubmissionSort> Sort);

public sealed record MailSubmissionQueryCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] MailSubmissionQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] long Position,
    Guid? AnchorId,
    [property: JsonRequired] bool AnchorCanMatch,
    [property: JsonRequired] long AnchorOffset,
    [property: JsonRequired] int Limit);

public sealed record MailSubmissionQueryChangesCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] MailSubmissionQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] string SinceState,
    long? MaxChanges);

public enum MailSubmissionQueryStatus
{
    Ok,
    AccountNotFound,
    Authorized,
    AnchorNotFound,
    CannotCalculateChanges,
    TooManyChanges,
}

public sealed record MailSubmissionQueryResult(
    MailSubmissionQueryStatus Status,
    string? State,
    long Position,
    [property: JsonRequired] IReadOnlyList<Guid> Ids,
    int Total);

public sealed record MailSubmissionIndexedId(Guid Id, int Index);

public sealed record MailSubmissionQueryChangesResult(
    MailSubmissionQueryStatus Status,
    string? State,
    [property: JsonRequired] IReadOnlyList<string> Removed,
    [property: JsonRequired] IReadOnlyList<MailSubmissionIndexedId> Added,
    int Total);
