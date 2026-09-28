// Internal mail change transport shapes are kept together deliberately.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailChangesCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] string SinceState,
    [property: JsonRequired] long? MaxChanges,
    [property: JsonRequired] bool AccountReferenceEligible);

public enum MailChangesStatus
{
    Ok,
    AccountNotFound,
    AccountNotSupported,
    CannotCalculateChanges,
}

public sealed record MailChangesResult(
    MailChangesStatus Status,
    string? OldState,
    string? NewState,
    bool HasMoreChanges,
    IReadOnlyList<string> CreatedKeys,
    IReadOnlyList<string> UpdatedKeys,
    IReadOnlyList<string> DestroyedKeys);

public static class MailChangeOperations
{
    public static bool TryGetFeature(MailOperationKind operation, out MailFeature feature)
    {
        feature = operation switch
        {
            MailOperationKind.ReadFolderChanges or MailOperationKind.ReadThreadChanges
                or MailOperationKind.ReadMessageChanges => MailFeature.Messages,
            MailOperationKind.ReadSenderIdentityChanges or MailOperationKind.ReadSubmissionChanges
                => MailFeature.Submission,
            MailOperationKind.ReadAddressBookChanges or MailOperationKind.ReadContactChanges
                => MailFeature.Contacts,
            _ => MailFeature.Unsupported,
        };
        return feature != MailFeature.Unsupported;
    }
}
