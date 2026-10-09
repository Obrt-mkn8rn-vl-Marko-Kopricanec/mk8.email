using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemDestination
{
    internal static GatewayEwsFolderReference Parse(XElement element)
    {
        var reference = GatewayEwsRequestParser.ParseReference(element);
        if (reference.ChangeKey is not null || reference.Distinguished && !Supported(reference.Id))
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        return reference;
    }

    internal static async Task<(Guid Folder, string? MailboxState, string? Error)> ResolveAsync(
        GatewayEwsClient application, ProtocolAuthentication authentication, JmapApplicationProfile profile,
        Guid account, GatewayEwsFolderReference reference, CancellationToken cancellationToken)
    {
        if (account == Guid.Empty) return (Guid.Empty, null, "ErrorFolderNotFound");
        if (!reference.Distinguished)
        {
            var code = !GatewayEwsFolderIdCodec.TryDecode(reference.Id, out var owner, out var folder) ? "ErrorInvalidIdMalformed"
                : owner != account ? "ErrorAccessDenied" : folder == Guid.Empty ? "ErrorAccessDenied" : null;
            return (folder, null, code);
        }
        if (reference.Mailbox is not null && !string.Equals(reference.Mailbox, profile.Username, StringComparison.OrdinalIgnoreCase))
            return (Guid.Empty, null, "ErrorAccessDenied");
        if (!Supported(reference.Id)) return (Guid.Empty, null, "ErrorFolderNotFound");
        var read = await application.ReadGraphAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
        if (read.Status != MailFolderReadStatus.Ok)
            return (Guid.Empty, null, read.Status == MailFolderReadStatus.RequestTooLarge ? "ErrorExceededFindCountLimit" : "ErrorFolderNotFound");
        var graph = new GatewayEwsFolderGraph(account, read.State!, read.Folders);
        var (target, error) = graph.Resolve(reference, profile.Username);
        // This native state is required on the later mutation. Resolving a name
        // here alone must not authorize writes after a committed role change.
        return (target, error is null ? graph.State : null, error);
    }

    private static bool Supported(string name) => name is "inbox" or "drafts" or "sentitems" or "deleteditems" or "junkemail";
}
