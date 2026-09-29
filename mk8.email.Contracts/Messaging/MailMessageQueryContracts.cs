// Message query criteria and their internal transport are deliberately grouped.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailMessageFilterOperator
{
    Condition,
    And,
    Or,
    Not,
}

public enum MailMessageFilterField
{
    InMailbox,
    InMailboxOtherThan,
    Before,
    After,
    MinSize,
    MaxSize,
    AllInThreadHaveKeyword,
    SomeInThreadHaveKeyword,
    NoneInThreadHaveKeyword,
    HasKeyword,
    NotKeyword,
    HasAttachment,
    Text,
    From,
    To,
    Cc,
    Bcc,
    Subject,
    Body,
    Header,
}

public sealed record MailMessageFilterTerm(
    [property: JsonRequired] MailMessageFilterField Field,
    [property: JsonRequired] string? Text,
    [property: JsonRequired] string? HeaderText,
    [property: JsonRequired] IReadOnlyList<string>? Values,
    [property: JsonRequired] DateTime? UtcDate,
    [property: JsonRequired] long? Number,
    [property: JsonRequired] bool? Flag);

public sealed record MailMessageFilter(
    [property: JsonRequired] MailMessageFilterOperator Operator,
    [property: JsonRequired] IReadOnlyList<MailMessageFilter>? Conditions,
    [property: JsonRequired] IReadOnlyList<MailMessageFilterTerm>? Terms);

public enum MailMessageSortField
{
    ReceivedAt,
    Size,
    From,
    To,
    Subject,
    SentAt,
    HasKeyword,
    AllInThreadHaveKeyword,
    SomeInThreadHaveKeyword,
}

public sealed record MailMessageSort(
    [property: JsonRequired] MailMessageSortField Field,
    [property: JsonRequired] bool IsAscending,
    [property: JsonRequired] string? Keyword,
    [property: JsonRequired] MailStringCollation Collation);

public sealed record MailMessageQueryCriteria(
    [property: JsonRequired] MailMessageFilter? Filter,
    [property: JsonRequired] IReadOnlyList<MailMessageSort> Sort,
    [property: JsonRequired] bool CollapseThreads);

public sealed record MailMessageQueryCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] MailMessageQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] long Position,
    [property: JsonRequired] string? AnchorId,
    [property: JsonRequired] long AnchorOffset,
    [property: JsonRequired] int Limit);

public sealed record MailMessageQueryChangesCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] MailMessageQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] string SinceState,
    [property: JsonRequired] long? MaxChanges);

public enum MailMessageQueryStatus
{
    Ok,
    AccountNotFound,
    Authorized,
    AnchorNotFound,
    CannotCalculateChanges,
    TooManyChanges,
}

public sealed record MailMessageQueryResult(
    MailMessageQueryStatus Status,
    string? State,
    long Position,
    [property: JsonRequired] IReadOnlyList<Guid> Ids,
    int Total);

public sealed record MailMessageIndexedId(Guid Id, int Index);

public sealed record MailMessageQueryChangesResult(
    MailMessageQueryStatus Status,
    string? State,
    [property: JsonRequired] IReadOnlyList<string> Removed,
    [property: JsonRequired] IReadOnlyList<MailMessageIndexedId> Added,
    int Total);
