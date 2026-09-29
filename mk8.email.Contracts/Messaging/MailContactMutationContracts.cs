// Contact mutation transports JSContact document values, not a JMAP method body.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailContactMutationCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool AccountReferenceParseable,
    [property: JsonRequired] bool AccountReferenceEligible,
    string? IfInState,
    [property: JsonRequired] IReadOnlyDictionary<string, Guid> AddressBookAliases,
    [property: JsonRequired] IReadOnlyList<MailContactCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailContactUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailContactDestroy> Destroys);

public sealed record MailContactCreate(
    string CreationId,
    bool HasId,
    Guid? AddressBookId,
    [property: JsonRequired] ApplicationValue Card);

public sealed record MailContactTarget(Guid? ExistingId, string? CreatedKey);

public sealed record MailContactPatchEntry(
    [property: JsonRequired] IReadOnlyList<string> Path,
    [property: JsonRequired] ApplicationValue Value);

public sealed record MailContactUpdate(
    string RequestedId,
    [property: JsonRequired] MailContactTarget Target,
    IReadOnlyList<MailContactPatchEntry>? Patch);

public sealed record MailContactDestroy(
    string RequestedId,
    [property: JsonRequired] MailContactTarget Target);

public enum MailContactMutationStatus { Ok, AccountNotFound, AccountNotSupported, StateMismatch }

public enum MailContactMutationError
{
    None,
    InvalidProperties,
    InvalidPatch,
    NotFound,
    OverQuota,
    WillDestroy,
}

public sealed record MailContactCreateOutcome(
    string CreationId,
    Guid? CardId,
    ApplicationValue? StoredCard,
    MailContactMutationError Error,
    IReadOnlyList<string>? InvalidProperties);

public sealed record MailContactUpdateOutcome(
    string RequestedId,
    Guid? CardId,
    ApplicationValue? RequestedCard,
    ApplicationValue? StoredCard,
    MailContactMutationError Error,
    IReadOnlyList<string>? InvalidProperties);

public sealed record MailContactDestroyOutcome(
    string RequestedId,
    Guid? CardId,
    MailContactMutationError Error);

public sealed record MailContactMutationResult(
    MailContactMutationStatus Status,
    string? OldState,
    string? NewState,
    [property: JsonRequired] IReadOnlyList<MailContactCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailContactUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailContactDestroyOutcome> Destroyed);
