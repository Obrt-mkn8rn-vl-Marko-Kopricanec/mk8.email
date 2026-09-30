// Folder mutations carry domain values and outcomes across the durable boundary.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailFolderMutationCommand(
    [property: JsonRequired] Guid AccountId,
    string? IfInState,
    bool RemoveEmailsOnDestroy,
    [property: JsonRequired] IReadOnlyList<MailFolderCreate> Creates,
    [property: JsonRequired] IReadOnlyList<MailFolderUpdate> Updates,
    [property: JsonRequired] IReadOnlyList<MailFolderDestroy> Destroys);

public sealed record MailFolderValues(string Name, string? ParentReference, string? Role,
    long SortOrder, bool IsSubscribed);

public sealed record MailFolderCreate(string CreationId, MailFolderValues? Values,
    MailFolderMutationFailure? Failure);

[Flags]
public enum MailFolderFields { None = 0, Name = 1, Parent = 2, Role = 4, SortOrder = 8, Subscription = 16 }

public enum MailFolderInvariant { Id, TotalMessages, UnreadMessages, TotalThreads, UnreadThreads, Rights }

public sealed record MailFolderExpectation(MailFolderInvariant Field, string? Identifier,
    long? Count, bool MatchesProtected, bool MatchesOrdinary);

public sealed record MailFolderPatch([property: JsonRequired] MailFolderFields Fields,
    MailFolderValues? Values, [property: JsonRequired] IReadOnlyList<MailFolderExpectation> Expectations,
    MailFolderMutationFailure? Failure);

public sealed record MailFolderUpdate(string RequestedId, [property: JsonRequired] MailFolderPatch Patch);

public sealed record MailFolderDestroy(string RequestedId);

public enum MailFolderMutationStatus { Ok, AccountNotFound, StateMismatch }

public enum MailFolderMutationError
{
    None,
    InvalidProperties,
    InvalidPatch,
    NotFound,
    Forbidden,
    MailboxHasChild,
    MailboxHasEmail,
}

public sealed record MailFolderMutationFailure(
    MailFolderMutationError Error,
    string? Description = null,
    IReadOnlyList<string>? Properties = null);

public sealed record MailFolderCreatedSnapshot(
    Guid Id,
    string Name,
    Guid? ParentId,
    string? Role,
    long SortOrder,
    bool IsSubscribed);

public sealed record MailFolderCreateOutcome(
    string CreationId,
    MailFolderCreatedSnapshot? Folder,
    MailFolderMutationFailure? Failure);

public sealed record MailFolderUpdateOutcome(
    string RequestedId,
    Guid? FolderId,
    MailFolderMutationFailure? Failure);

public sealed record MailFolderDestroyOutcome(
    string RequestedId,
    Guid? FolderId,
    MailFolderMutationFailure? Failure);

public sealed record MailFolderMutationResult(
    MailFolderMutationStatus Status,
    string? OldState,
    string? NewState,
    [property: JsonRequired] IReadOnlyList<MailFolderCreateOutcome> Created,
    [property: JsonRequired] IReadOnlyList<MailFolderUpdateOutcome> Updated,
    [property: JsonRequired] IReadOnlyList<MailFolderDestroyOutcome> Destroyed);
