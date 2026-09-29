// Identity mutation commands and outcomes are grouped as one internal transport.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailIdentityMutationCommand(
    [property: JsonRequired] Guid AccountId,
    string? IfInState,
    [property: JsonRequired] IReadOnlyList<MailIdentityCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailIdentityUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailIdentityDestroy> Destroys);

public sealed record MailIdentityValues(
    string Name,
    string? Email,
    MailIdentityAddressListSnapshot? ReplyTo,
    MailIdentityAddressListSnapshot? Bcc,
    string TextSignature,
    string HtmlSignature);

public sealed record MailIdentityCreate(string CreationId, MailIdentityValues? Values);

public sealed record MailIdentityTarget(Guid? ExistingId, string? CreatedKey);

public sealed record MailIdentityPatch(
    bool SetName, string? Name,
    bool SetReplyTo, MailIdentityAddressListSnapshot? ReplyTo,
    bool SetBcc, MailIdentityAddressListSnapshot? Bcc,
    bool SetTextSignature, string? TextSignature,
    bool SetHtmlSignature, string? HtmlSignature,
    bool AssertId, string? Id,
    bool AssertEmail, string? Email,
    bool AssertMayDelete, bool? MayDelete);

public sealed record MailIdentityUpdate(
    string RequestedId,
    [property: JsonRequired] MailIdentityTarget Target,
    MailIdentityPatch? Patch);

public sealed record MailIdentityDestroy(
    string RequestedId,
    [property: JsonRequired] MailIdentityTarget Target);

public enum MailIdentityMutationStatus { Ok, AccountNotFound, StateMismatch }

public enum MailIdentityMutationError
{
    None,
    Skipped,
    InvalidProperties,
    ForbiddenFrom,
    NotFound,
    WillDestroy,
    Forbidden,
}

public enum MailIdentityImmutableField { Id, Email, MayDelete }

public sealed record MailIdentityCreateOutcome(string CreationId, Guid? IdentityId,
    MailIdentityMutationError Error);

public sealed record MailIdentityUpdateOutcome(string RequestedId, Guid? IdentityId,
    MailIdentityMutationError Error, IReadOnlyList<MailIdentityImmutableField>? InvalidFields);

public sealed record MailIdentityDestroyOutcome(string RequestedId, Guid? IdentityId,
    MailIdentityMutationError Error);

public sealed record MailIdentityMutationResult(
    MailIdentityMutationStatus Status,
    string? OldState,
    string? NewState,
    [property: JsonRequired] IReadOnlyList<MailIdentityCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailIdentityUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailIdentityDestroyOutcome> Destroyed);
