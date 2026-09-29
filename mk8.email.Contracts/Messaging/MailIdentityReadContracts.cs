// Domain snapshots and their internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailIdentityReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid>? IdentityIds);

public enum MailIdentityReadStatus
{
    Ok,
    AccountNotFound,
    RequestTooLarge,
}

public sealed record MailIdentityAddressSnapshot(string? Name, string Email);

public sealed record MailIdentityAddressListSnapshot(IReadOnlyList<MailIdentityAddressSnapshot> Addresses);

public sealed record MailIdentitySnapshot(
    Guid Id,
    string Name,
    string Email,
    MailIdentityAddressListSnapshot? ReplyTo,
    MailIdentityAddressListSnapshot? Bcc,
    string TextSignature,
    string HtmlSignature,
    bool MayDelete);

public sealed record MailIdentityReadResult(
    MailIdentityReadStatus Status,
    string? State,
    IReadOnlyList<MailIdentitySnapshot> Identities);
