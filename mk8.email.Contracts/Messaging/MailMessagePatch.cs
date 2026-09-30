// Typed map mutations and MIME observations constitute one message-update command.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public enum MailMessageFlagField { Folders, Keywords }
public enum MailMessageFlagChangeKind { Replace, Clear, Set, Remove }
public enum MailMessageFlagValue { Enabled, Disabled, Malformed }

public sealed record MailMessageFlagEntry(
    [property: JsonRequired] string Key,
    [property: JsonRequired] MailMessageFlagValue Value);

public sealed record MailMessageFlagChange(
    [property: JsonRequired] MailMessageFlagField Field,
    [property: JsonRequired] MailMessageFlagChangeKind Kind,
    [property: JsonRequired] IReadOnlyList<MailMessageFlagEntry> Entries,
    [property: JsonRequired] bool InvalidContainer);

public enum MailMessageObservationField
{
    Id, ContentReference, ThreadReference, Length, ArrivalTime, AttachmentPresence,
    Summary, RawHeaders, Header, BodyTree, PlainParts, HtmlParts, Attachments, TextValues,
}

public enum MailHeaderValueForm { Raw, Text, Addresses, GroupedAddresses, MessageIds, Date, URLs }

public sealed record MailHeaderObservation(
    [property: JsonRequired] string Name,
    [property: JsonRequired] MailHeaderValueForm Form,
    [property: JsonRequired] bool All);

public enum MailMimePartField
{
    Path, ContentReference, DecodedLength, RawHeaders, FileName, MediaType, Charset,
    Disposition, ContentId, Languages, Location, Children, Header,
}

public sealed record MailMimeFieldSelection(
    [property: JsonRequired] string Key,
    [property: JsonRequired] MailMimePartField Field,
    MailHeaderObservation? Header);

public sealed record MailMessageValueChange(
    [property: JsonRequired] IReadOnlyList<string> Path,
    [property: JsonRequired] ApplicationValue Value);

public sealed record MailMessageAssertion(
    [property: JsonRequired] string Label,
    [property: JsonRequired] MailMessageObservationField Field,
    MailHeaderObservation? Header,
    [property: JsonRequired] IReadOnlyList<MailMessageValueChange> Changes,
    [property: JsonRequired] bool MatchVisibleTextValues);

public sealed record MailMessagePatch(
    [property: JsonRequired] IReadOnlyList<MailMessageFlagChange> Flags,
    [property: JsonRequired] IReadOnlyList<MailMessageAssertion> Assertions,
    [property: JsonRequired] IReadOnlyList<MailMimeFieldSelection> PartFields,
    [property: JsonRequired] int SummaryRunes,
    [property: JsonRequired] bool RequiresMime,
    MailMessageMutationFailure? Failure);

public enum MailMessageFragmentStage { Ok, UnknownObservation, UnsupportedView, InvalidPath }

public sealed record MailMessagePatchFragment(
    [property: JsonRequired] string CorrelationKey,
    [property: JsonRequired] IReadOnlyList<string> CollisionPath,
    [property: JsonRequired] MailMessageFragmentStage Stage,
    [property: JsonRequired] MailMessagePatch Patch);
