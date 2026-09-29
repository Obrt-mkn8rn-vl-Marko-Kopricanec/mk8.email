// Address-book mutations and outcomes are one internal transport contract.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailAddressBookMutationCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool AccountReferenceEligible,
    string? IfInState,
    bool OnDestroyRemoveContents,
    MailAddressBookTarget? OnSuccessSetIsDefault,
    [property: JsonRequired] IReadOnlyList<MailAddressBookCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailAddressBookUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailAddressBookDestroy> Destroys);

public sealed record MailAddressBookValues(string? Name, string? Description,
    long SortOrder, bool IsSubscribed);

public sealed record MailAddressBookCreate(string CreationId,
    bool HasNonNullShareWith, MailAddressBookValues? Values);

public sealed record MailAddressBookTarget(Guid? ExistingId, string? CreatedKey);

public sealed record MailAddressBookRightsAssertion(
    bool CheckMayRead, bool? MayRead,
    bool CheckMayWrite, bool? MayWrite,
    bool CheckMayShare, bool? MayShare,
    bool CheckMayDelete, bool? MayDelete);

public sealed record MailAddressBookPatch(
    bool SetName, string? Name,
    bool SetDescription, string? Description,
    bool SetSortOrder, long? SortOrder,
    bool SetIsSubscribed, bool? IsSubscribed,
    bool AssertId, string? Id,
    bool AssertIsDefault, bool? IsDefault,
    MailAddressBookRightsAssertion? Rights,
    bool HasNonNullShareWith);

public sealed record MailAddressBookUpdate(
    string RequestedId,
    [property: JsonRequired] MailAddressBookTarget Target,
    MailAddressBookPatch? Patch);

public sealed record MailAddressBookDestroy(
    string RequestedId,
    [property: JsonRequired] MailAddressBookTarget Target);

public enum MailAddressBookMutationStatus { Ok, AccountNotFound, AccountNotSupported, StateMismatch }

public enum MailAddressBookMutationError
{
    None,
    Skipped,
    InvalidProperties,
    Forbidden,
    OverQuota,
    NotFound,
    WillDestroy,
    AddressBookHasContents,
}

public enum MailAddressBookField
{
    Id,
    Name,
    Description,
    SortOrder,
    IsDefault,
    IsSubscribed,
    ShareWith,
    MyRights,
}

public sealed record MailAddressBookCreateOutcome(string CreationId,
    MailAddressBookSnapshot? Book, MailAddressBookMutationError Error,
    IReadOnlyList<MailAddressBookField>? InvalidFields);

public sealed record MailAddressBookUpdateOutcome(string RequestedId,
    MailAddressBookSnapshot? Book, MailAddressBookMutationError Error,
    IReadOnlyList<MailAddressBookField>? InvalidFields);

public sealed record MailAddressBookDestroyOutcome(string RequestedId, Guid? BookId,
    MailAddressBookMutationError Error);

public sealed record MailAddressBookMutationResult(
    MailAddressBookMutationStatus Status,
    string? OldState,
    string? NewState,
    [property: JsonRequired] IReadOnlyList<MailAddressBookCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailAddressBookUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailAddressBookDestroyOutcome> Destroyed,
    [property: JsonRequired] IReadOnlyList<MailAddressBookSnapshot> DefaultChanges);
