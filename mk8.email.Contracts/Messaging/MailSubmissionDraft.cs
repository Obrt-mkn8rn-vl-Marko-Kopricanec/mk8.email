// Coupled submission values and assertions form one internal transport contract.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailEnvelopeAddressIssue { None, InvalidShape, InvalidAddress, InvalidParameters }

public sealed record MailEnvelopeAddressDraft(
    [property: JsonRequired] string Address,
    [property: JsonRequired] IReadOnlyDictionary<string, string>? Parameters,
    [property: JsonRequired] MailEnvelopeAddressIssue Issue);

public sealed record MailSubmissionEnvelopeDraft(
    [property: JsonRequired] MailEnvelopeAddressDraft Sender,
    [property: JsonRequired] IReadOnlyList<MailEnvelopeAddressDraft> Recipients,
    [property: JsonRequired] bool InvalidShape);

public sealed record MailSubmissionDraft(
    [property: JsonRequired] string? IdentityReference,
    [property: JsonRequired] string? MessageReference,
    [property: JsonRequired] bool InvalidFields,
    [property: JsonRequired] MailSubmissionEnvelopeDraft? Envelope);

public sealed record MailEnvelopeAddress(
    [property: JsonRequired] string Address,
    [property: JsonRequired] IReadOnlyDictionary<string, string>? Parameters);

public sealed record MailSubmissionEnvelope(
    [property: JsonRequired] MailEnvelopeAddress Sender,
    [property: JsonRequired] IReadOnlyList<MailEnvelopeAddress> Recipients);

public enum MailSubmissionObservationField
{
    Unknown, Id, IdentityReference, MessageReference, ThreadReference, Envelope,
    SendTime, UndoState, Delivery, DeliveryReceipts, ReadReceipts,
}

public enum MailSubmissionEmailIssue
{
    RawHeaders, BodyTree, HeaderDate, From, Sender, ReplyTo, To, Cc, Bcc,
    MessageId, InReplyTo, References, Subject, ResentDate, ResentFrom, ResentSender,
    ResentTo, ResentCc, ResentBcc, ResentMessageId, ResentReplyTo,
}

public sealed record MailSubmissionAssertion(
    [property: JsonRequired] string Label,
    [property: JsonRequired] MailSubmissionObservationField Field,
    [property: JsonRequired] IReadOnlyList<MailMessageValueChange> Changes);

public sealed record MailSubmissionPatch(
    [property: JsonRequired] IReadOnlyList<MailSubmissionAssertion> Assertions,
    MailSubmissionMutationFailure? Failure);
