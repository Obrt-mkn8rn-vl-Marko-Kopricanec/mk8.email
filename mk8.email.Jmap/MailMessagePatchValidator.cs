using mk8.email.Contracts.Messaging;
using System.Buffers;

namespace mk8.email.Jmap;

internal static class MailMessagePatchValidator
{
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789ABCDEF");
    public static bool IsValid(MailMessagePatch patch)
    {
        if (patch.Flags is null || patch.Assertions is null || patch.PartFields is null
            || patch.SummaryRunes is < 0 or > 256 || !ValidFailure(patch.Failure)
            || patch.Assertions.Count > 0 && !patch.RequiresMime
            || patch.Failure is not null && (patch.Flags.Count != 0 || patch.Assertions.Count != 0 || patch.PartFields.Count != 0))
            return false;
        if (patch.Flags.Any(change => !ValidFlags(change)) || patch.Assertions.Any(item => !ValidAssertion(item))
            || patch.Assertions.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() != patch.Assertions.Count
            || patch.PartFields.Any(item => !ValidPartField(item))
            || patch.PartFields.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != patch.PartFields.Count)
            return false;
        foreach (var assertion in patch.Assertions)
            foreach (var change in assertion.Changes) _ = ApplicationValueCodec.Decode(change.Value);
        return true;
    }

    public static bool ValidFragments(IReadOnlyList<MailMessagePatchFragment>? fragments) =>
        fragments is not null && fragments.All(item => item is not null && item.CorrelationKey is not null
            && item.CollisionPath is not null && item.CollisionPath.All(token => token is not null && token.Length > 0)
            && Enum.IsDefined(item.Stage) && item.Patch is not null && IsValid(item.Patch)
            && (item.Stage != MailMessageFragmentStage.Ok || item.CollisionPath.Count > 0)
            && (item.Stage == MailMessageFragmentStage.Ok) == (item.Patch.Failure is null));

    private static bool ValidFlags(MailMessageFlagChange? change) =>
        change is not null && Enum.IsDefined(change.Field) && Enum.IsDefined(change.Kind) && change.Entries is not null
        && change.Entries.All(item => item is not null && item.Key is not null && Enum.IsDefined(item.Value))
        && change.Entries.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() == change.Entries.Count
        && (change.Kind switch
        {
            MailMessageFlagChangeKind.Clear => change.Entries.Count == 0 && !change.InvalidContainer,
            MailMessageFlagChangeKind.Replace => !change.InvalidContainer || change.Entries.Count == 0,
            _ => change.Entries.Count == 1 && !change.InvalidContainer,
        });

    private static bool ValidAssertion(MailMessageAssertion? item) =>
        item is not null && !string.IsNullOrEmpty(item.Label) && Enum.IsDefined(item.Field)
        && (item.Field == MailMessageObservationField.Header ? ValidHeader(item.Header) : item.Header is null)
        && item.Changes is not null && item.Changes.All(change => change is not null
            && change.Path is not null && change.Path.All(token => token is not null && token.Length > 0) && change.Value is not null)
        && (!item.MatchVisibleTextValues || item.Field == MailMessageObservationField.TextValues
            && item.Changes.Any(change => change.Path.Count == 0));

    private static bool ValidPartField(MailMimeFieldSelection? item) =>
        item is not null && Enum.IsDefined(item.Field) && item.Key is not null
        && (item.Field == MailMimePartField.Header ? item.Key.Length == 70 && item.Key.StartsWith("Header", StringComparison.Ordinal)
            && item.Key.AsSpan(6).IndexOfAnyExcept(HexCharacters) < 0
            && ValidHeader(item.Header) : string.Equals(item.Key, item.Field.ToString(), StringComparison.Ordinal) && item.Header is null);

    private static bool ValidHeader(MailHeaderObservation? header) => header is not null && Enum.IsDefined(header.Form)
        && !string.IsNullOrEmpty(header.Name) && header.Name.All(character => character is >= (char)33 and <= (char)126 && character != ':');

    private static bool ValidFailure(MailMessageMutationFailure? failure) => failure is null
        || failure.Error is MailMessageMutationError.InvalidProperties or MailMessageMutationError.InvalidPatch;
}
