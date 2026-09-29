// Contact query commands and results form one versioned transport contract.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailContactFilterOperator
{
    Condition,
    And,
    Or,
    Not,
}

public enum MailContactFilterField
{
    InAddressBook,
    Uid,
    HasMember,
    Kind,
    Text,
    Name,
    GivenName,
    Surname,
    Surname2,
    Nickname,
    Organization,
    Email,
    Phone,
    OnlineService,
    Address,
    Note,
}

public sealed record MailContactFilterTerm(
    MailContactFilterField Field,
    [property: JsonRequired] string Value);

public sealed record MailContactFilter(
    [property: JsonRequired] MailContactFilterOperator Operator,
    [property: JsonRequired] IReadOnlyList<MailContactFilter>? Conditions,
    [property: JsonRequired] IReadOnlyList<MailContactFilterTerm>? Terms,
    DateTime? CreatedBefore,
    DateTime? CreatedAfter,
    DateTime? UpdatedBefore,
    DateTime? UpdatedAfter);

public enum MailContactSortField
{
    Created,
    Updated,
    GivenName,
    Surname,
    Surname2,
}

public sealed record MailContactSort(
    [property: JsonRequired] MailContactSortField Field,
    [property: JsonRequired] bool IsAscending,
    [property: JsonRequired] MailStringCollation Collation);

public sealed record MailContactQueryCriteria(
    [property: JsonRequired] MailContactFilter? Filter,
    [property: JsonRequired] IReadOnlyList<MailContactSort> Sort);

public sealed record MailContactQueryCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool AccountReferenceParseable,
    [property: JsonRequired] bool AccountReferenceEligible,
    [property: JsonRequired] MailContactQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] long Position,
    [property: JsonRequired] Guid? AnchorId,
    [property: JsonRequired] bool AnchorCanMatch,
    [property: JsonRequired] long AnchorOffset,
    [property: JsonRequired] int Limit);

public sealed record MailContactQueryChangesCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool AccountReferenceParseable,
    [property: JsonRequired] bool AccountReferenceEligible,
    [property: JsonRequired] MailContactQueryCriteria Criteria,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] string SinceState,
    [property: JsonRequired] long? MaxChanges);

public enum MailContactQueryStatus
{
    Ok,
    AccountNotFound,
    AccountNotSupported,
    Authorized,
    AnchorNotFound,
    CannotCalculateChanges,
    TooManyChanges,
}

public sealed record MailContactQueryResult(
    MailContactQueryStatus Status,
    string? State,
    long Position,
    [property: JsonRequired] IReadOnlyList<Guid> Ids,
    int Total);

public sealed record MailContactIndexedId(Guid Id, int Index);

public sealed record MailContactQueryChangesResult(
    MailContactQueryStatus Status,
    string? State,
    [property: JsonRequired] IReadOnlyList<string> Removed,
    [property: JsonRequired] IReadOnlyList<MailContactIndexedId> Added,
    int Total);
