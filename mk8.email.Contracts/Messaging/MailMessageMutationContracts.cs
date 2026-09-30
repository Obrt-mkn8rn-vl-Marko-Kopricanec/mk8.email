// Mutation outcome records are grouped with their typed command.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailMessageMutationCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] string? IfInState,
    [property: JsonRequired] IReadOnlyList<MailMessageCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailMessageUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailMessageDestroy> Destroys);

public sealed record MailMessageCreate(
    [property: JsonRequired] string CreationId,
    [property: JsonRequired] MailMessageDraft Draft);

public sealed record MailMessageUpdate(
    [property: JsonRequired] string RequestedId,
    [property: JsonRequired] ApplicationValue Patch);

public sealed record MailMessageDestroy([property: JsonRequired] string RequestedId);

public enum MailMessageMutationStatus { Ok, AccountNotFound, StateMismatch }

public enum MailMessageMutationError
{
    None,
    InvalidProperties,
    InvalidPatch,
    NotFound,
    TooManyMailboxes,
    BlobNotFound,
    TooManyKeywords,
    InvalidEmail,
    TooLarge,
    OverQuota,
}

public sealed record MailMessageMutationFailure(
    [property: JsonRequired] MailMessageMutationError Error,
    string? Description,
    IReadOnlyList<string>? Properties,
    IReadOnlyList<string>? MissingBlobIds);

public sealed record MailMessageCreatedSnapshot(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string StoredThreadId,
    [property: JsonRequired] int SizeBytes);

public sealed record MailMessageCreateOutcome(
    [property: JsonRequired] string CreationId,
    MailMessageCreatedSnapshot? Message,
    MailMessageMutationFailure? Failure);

public sealed record MailMessageUpdateOutcome(
    [property: JsonRequired] string RequestedId,
    Guid? MessageId,
    MailMessageMutationFailure? Failure);

public sealed record MailMessageDestroyOutcome(
    [property: JsonRequired] string RequestedId,
    Guid? MessageId,
    MailMessageMutationFailure? Failure);

public sealed record MailMessageMutationResult(
    [property: JsonRequired] MailMessageMutationStatus Status,
    [property: JsonRequired] string? OldState,
    [property: JsonRequired] string? NewState,
    [property: JsonRequired] IReadOnlyList<MailMessageCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailMessageUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailMessageDestroyOutcome> Destroyed);
