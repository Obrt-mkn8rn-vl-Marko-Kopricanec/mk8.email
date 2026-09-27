using System.Collections.Frozen;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapOperationCodec
{
    private static readonly FrozenDictionary<MailOperationKind, string> Names = new Dictionary<MailOperationKind, string>
    {
        [MailOperationKind.Failure] = "error",
        [MailOperationKind.Echo] = "Core/echo",
        [MailOperationKind.ReadFolders] = "Mailbox/get",
        [MailOperationKind.FindFolders] = "Mailbox/query",
        [MailOperationKind.ReadFolderChanges] = "Mailbox/changes",
        [MailOperationKind.FindFolderChanges] = "Mailbox/queryChanges",
        [MailOperationKind.MutateFolders] = "Mailbox/set",
        [MailOperationKind.ReadThreads] = "Thread/get",
        [MailOperationKind.ReadThreadChanges] = "Thread/changes",
        [MailOperationKind.ReadMessages] = "Email/get",
        [MailOperationKind.FindMessages] = "Email/query",
        [MailOperationKind.ReadMessageChanges] = "Email/changes",
        [MailOperationKind.FindMessageChanges] = "Email/queryChanges",
        [MailOperationKind.MutateMessages] = "Email/set",
        [MailOperationKind.ImportMessages] = "Email/import",
        [MailOperationKind.CopyMessages] = "Email/copy",
        [MailOperationKind.ParseMessages] = "Email/parse",
        [MailOperationKind.ReadSearchSnippets] = "SearchSnippet/get",
        [MailOperationKind.ReadSenderIdentities] = "Identity/get",
        [MailOperationKind.ReadSenderIdentityChanges] = "Identity/changes",
        [MailOperationKind.MutateSenderIdentities] = "Identity/set",
        [MailOperationKind.ReadSubmissions] = "EmailSubmission/get",
        [MailOperationKind.FindSubmissions] = "EmailSubmission/query",
        [MailOperationKind.ReadSubmissionChanges] = "EmailSubmission/changes",
        [MailOperationKind.FindSubmissionChanges] = "EmailSubmission/queryChanges",
        [MailOperationKind.MutateSubmissions] = "EmailSubmission/set",
        [MailOperationKind.ReadVacationSettings] = "VacationResponse/get",
        [MailOperationKind.MutateVacationSettings] = "VacationResponse/set",
        [MailOperationKind.ReadNotificationSubscriptions] = "PushSubscription/get",
        [MailOperationKind.MutateNotificationSubscriptions] = "PushSubscription/set",
        [MailOperationKind.CopyBinaryObjects] = "Blob/copy",
        [MailOperationKind.ReadAddressBooks] = "AddressBook/get",
        [MailOperationKind.ReadAddressBookChanges] = "AddressBook/changes",
        [MailOperationKind.MutateAddressBooks] = "AddressBook/set",
        [MailOperationKind.ReadContacts] = "ContactCard/get",
        [MailOperationKind.FindContacts] = "ContactCard/query",
        [MailOperationKind.ReadContactChanges] = "ContactCard/changes",
        [MailOperationKind.FindContactChanges] = "ContactCard/queryChanges",
        [MailOperationKind.MutateContacts] = "ContactCard/set",
        [MailOperationKind.CopyContacts] = "ContactCard/copy",
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<string, MailOperationKind> Operations =
        Names.ToFrozenDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);

    public static MailOperationKind DecodeCall(string name)
    {
        var operation = DecodeReference(name);
        return operation == MailOperationKind.Failure ? MailOperationKind.None : operation;
    }

    public static MailOperationKind DecodeReference(string name) =>
        Operations.TryGetValue(name, out var operation) ? operation : MailOperationKind.None;

    public static string Render(MailOperationKind operation) => Names.TryGetValue(operation, out var name)
        ? name : throw new InvalidOperationException("Application returned an unrecognized operation identifier.");
}
