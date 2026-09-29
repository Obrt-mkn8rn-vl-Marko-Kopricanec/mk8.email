// Contact-read command and snapshots are deliberately grouped as one transport contract.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailContactReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool AccountReferenceParseable,
    [property: JsonRequired] bool AccountReferenceEligible,
    [property: JsonRequired] IReadOnlyList<Guid>? CardIds);

public enum MailContactReadStatus
{
    Ok,
    AccountNotFound,
    AccountNotSupported,
    RequestTooLarge,
}

public sealed record MailContactCardSnapshot(
    Guid Id,
    Guid AddressBookId,
    [property: JsonRequired] string Uid,
    [property: JsonRequired] string CardJson);

public sealed record MailContactReadResult(
    MailContactReadStatus Status,
    string? State,
    [property: JsonRequired] IReadOnlyList<MailContactCardSnapshot> Cards);
