// Submission commands and correlated outcomes constitute one transport contract.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailSubmissionMutationCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] string? IfInState,
    [property: JsonRequired] IReadOnlyList<MailSubmissionCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailSubmissionUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailSubmissionDestroy> Destroys,
    [property: JsonRequired] IReadOnlyList<MailSubmissionEmailUpdate> OnSuccessUpdates,
    [property: JsonRequired] IReadOnlyList<MailSubmissionEmailDestroy> OnSuccessDestroys);

public sealed record MailSubmissionCreate(
    [property: JsonRequired] string CreationId,
    [property: JsonRequired] MailSubmissionDraft Draft);

public sealed record MailSubmissionUpdate(
    [property: JsonRequired] string RequestedId,
    [property: JsonRequired] MailSubmissionPatch Patch);

public sealed record MailSubmissionDestroy([property: JsonRequired] string RequestedId);

public sealed record MailSubmissionEmailUpdate(
    [property: JsonRequired] string RequestedSubmissionId,
    [property: JsonRequired] IReadOnlyList<MailMessagePatchFragment> Fragments);

public sealed record MailSubmissionEmailDestroy([property: JsonRequired] string RequestedSubmissionId);

public enum MailSubmissionMutationStatus { Ok, AccountNotFound, StateMismatch }

public enum MailSubmissionMutationError
{
    None,
    InvalidProperties,
    InvalidPatch,
    NotFound,
    CannotUnsend,
    InvalidEmail,
    ForbiddenFrom,
    ForbiddenMailFrom,
    NoRecipients,
    TooManyRecipients,
    InvalidRecipients,
    TooLarge,
}

public sealed record MailSubmissionMutationFailure(
    [property: JsonRequired] MailSubmissionMutationError Error,
    string? Description,
    IReadOnlyList<string>? Properties,
    IReadOnlyList<string>? InvalidRecipients,
    long? MaxSize,
    int? MaxRecipients,
    IReadOnlyList<MailSubmissionEmailIssue>? EmailIssues = null);

public sealed record MailSubmissionCreateOutcome(
    [property: JsonRequired] string CreationId,
    MailSubmissionSnapshot? Submission,
    MailSubmissionMutationFailure? Failure);

public sealed record MailSubmissionUpdateOutcome(
    [property: JsonRequired] string RequestedId,
    Guid? SubmissionId,
    MailSubmissionMutationFailure? Failure);

public sealed record MailSubmissionDestroyOutcome(
    [property: JsonRequired] string RequestedId,
    Guid? SubmissionId,
    MailSubmissionMutationFailure? Failure);

public sealed record MailSubmissionMutationResult(
    [property: JsonRequired] MailSubmissionMutationStatus Status,
    [property: JsonRequired] string? OldState,
    [property: JsonRequired] string? NewState,
    [property: JsonRequired] IReadOnlyList<MailSubmissionCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailSubmissionUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailSubmissionDestroyOutcome> Destroyed,
    [property: JsonRequired] MailMessageMutationCommand? ImplicitMessageCommand,
    [property: JsonRequired] MailMessageMutationResult? ImplicitMessageResult);
