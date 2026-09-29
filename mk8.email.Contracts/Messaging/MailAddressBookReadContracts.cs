// Domain snapshots and their internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailAddressBookReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid>? BookIds,
    [property: JsonRequired] bool AccountReferenceEligible);

public enum MailAddressBookReadStatus
{
    Ok,
    AccountNotFound,
    AccountNotSupported,
    RequestTooLarge,
}

public sealed record MailAddressBookSnapshot(
    Guid Id,
    string Name,
    string? Description,
    long SortOrder,
    bool IsDefault,
    bool IsSubscribed,
    bool IsProtected);

public sealed record MailAddressBookReadResult(
    MailAddressBookReadStatus Status,
    string? State,
    IReadOnlyList<MailAddressBookSnapshot> Books);
