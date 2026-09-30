// Submission domain snapshots and their internal transport command are grouped deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailSubmissionReadCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] IReadOnlyList<Guid>? SubmissionIds,
    [property: JsonRequired] bool IncludeDeliveryStatus);

public enum MailSubmissionReadStatus
{
    Ok,
    AccountNotFound,
    RequestTooLarge,
}

public enum MailSubmissionDeliveryState
{
    Unknown,
    Pending,
    DeliveredLocal,
    DeliveredRemote,
    PermanentFailure,
    Quarantined,
}

public sealed record MailSubmissionDeliverySnapshot(
    string Recipient,
    MailSubmissionDeliveryState State,
    string? LastError);

public sealed record MailSubmissionSnapshot(
    Guid Id,
    string IdentityId,
    string EmailId,
    string ThreadId,
    MailSubmissionEnvelope? Envelope,
    string EnvelopeSender,
    IReadOnlyList<string> EnvelopeRecipients,
    DateTime SendAt,
    string UndoStatus,
    IReadOnlyList<MailSubmissionDeliverySnapshot>? DeliveryRecipients);

public sealed record MailSubmissionReadResult(
    MailSubmissionReadStatus Status,
    string? State,
    IReadOnlyList<MailSubmissionSnapshot> Submissions);
