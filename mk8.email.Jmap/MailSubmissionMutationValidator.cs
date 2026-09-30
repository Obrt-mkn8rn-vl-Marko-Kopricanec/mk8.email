using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailSubmissionMutationValidator
{
    public static bool ValidDraft(MailSubmissionDraft value) => value.Envelope is null ||
        value.Envelope.Sender is not null && ValidAddress(value.Envelope.Sender)
        && value.Envelope.Recipients is not null && value.Envelope.Recipients.All(ValidAddress);

    private static bool ValidAddress(MailEnvelopeAddressDraft? value) => value is not null && value.Address is not null
        && Enum.IsDefined(value.Issue) && (value.Parameters is null || value.Parameters.All(item => item.Value is not null));

    public static bool ValidPatch(MailSubmissionPatch value)
    {
        if (value.Assertions is null || value.Failure is not null
            && (value.Failure.Error != MailSubmissionMutationError.InvalidPatch || value.Assertions.Count != 0)
            || value.Assertions.Any(item => item is null || string.IsNullOrEmpty(item.Label) || !Enum.IsDefined(item.Field)
                || item.Changes is null || item.Changes.Count == 0 || item.Changes.Any(change => change is null
                    || change.Value is null || change.Path is null || change.Path.Any(token => string.IsNullOrEmpty(token))))
            || value.Assertions.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() != value.Assertions.Count) return false;
        foreach (var assertion in value.Assertions)
            foreach (var change in assertion.Changes) _ = ApplicationValueCodec.Decode(change.Value);
        return true;
    }
}
