// Contact-copy command and result types are deliberately grouped as one transport contract.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailContactCopyCommand(
    [property: JsonRequired] Guid SourceAccountId,
    [property: JsonRequired] bool SourceReferenceParseable,
    [property: JsonRequired] bool SourceReferenceEligible,
    [property: JsonRequired] Guid TargetAccountId,
    [property: JsonRequired] bool TargetReferenceParseable,
    [property: JsonRequired] bool TargetReferenceEligible,
    string? IfFromInState,
    string? IfInState,
    [property: JsonRequired] IReadOnlyList<string> CreationIds);

public enum MailContactCopyStatus
{
    Ok,
    FromAccountNotFound,
    FromAccountNotSupported,
    AccountNotFound,
    AccountNotSupported,
    StateMismatch,
}

public sealed record MailContactCopyResult(
    MailContactCopyStatus Status,
    string? State);
