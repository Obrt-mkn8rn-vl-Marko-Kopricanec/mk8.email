using System.Xml;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFindItemResponse
{
    internal static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, GatewayEwsFolderGraph? graph,
        string missingGraphError, CancellationToken cancellationToken)
    {
        var responses = new XElement(GatewayEwsSoap.Messages + "ResponseMessages");
        var pages = new Dictionary<Guid, (XElement? Root, string? Error, string? State)>();
        string? state = null;
        foreach (var reference in request.Folders)
        {
            var (folder, error) = graph is null ? (Guid.Empty, missingGraphError) : graph.Resolve(reference, profile.Username);
            XElement? root = null;
            if (error is null)
            {
                if (!pages.TryGetValue(folder, out var page))
                {
                    page = folder == Guid.Empty ? (Root(request, account, "", 0, []), null, null)
                        : await ReadPageAsync(application, authentication, profile, account, folder, request, cancellationToken).ConfigureAwait(false);
                    pages.Add(folder, page);
                }
                if (page.State is not null)
                {
                    if (state is not null && !string.Equals(state, page.State, StringComparison.Ordinal)) Busy();
                    state = page.State;
                }
                (root, error, _) = page;
            }
            responses.Add(Response(root, error));
        }
        if (state is not null)
        {
            // Recheck parent membership/roles/counts after the independently committed query/read operations.
            var finalGraph = await application.ReadGraphAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
            if (finalGraph.Status != MailFolderReadStatus.Ok || !string.Equals(finalGraph.State, graph!.State, StringComparison.Ordinal)) Busy();
        }
        return GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "FindItemResponse", responses));
    }

    private static async Task<(XElement? Root, string? Error, string? State)> ReadPageAsync(GatewayEwsClient application,
        ProtocolAuthentication authentication, JmapApplicationProfile profile, Guid account, Guid folder,
        GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var limit = Math.Min(request.Limit, profile.Limits.MaxObjectsInGet);
        var query = await application.QueryItemsAsync(authentication, profile, account, folder, request.Offset, limit, cancellationToken,
            request.Restriction, request.SortOrder).ConfigureAwait(false);
        if (query.Status != MailMessageQueryStatus.Ok) return (null, "ErrorFolderNotFound", null);
        if (!request.Indexed && query.Total > limit) return (null, "ErrorExceededFindCountLimit", query.State);
        IReadOnlyList<MailMessageSnapshot> items = [];
        if (query.Ids.Count != 0)
        {
            var read = await application.ReadItemsAsync(authentication, profile, account, query.Ids, cancellationToken).ConfigureAwait(false);
            if (read.Status == MailMessageReadStatus.RequestTooLarge) return (null, "ErrorDataSizeLimitExceeded", query.State);
            if (read.Status != MailMessageReadStatus.Ok || read.Messages.Count != query.Ids.Count
                || !string.Equals(read.State, query.State, StringComparison.Ordinal)
                || read.Messages.Any(item => item.Value.Stored!.FolderId != folder)) Busy();
            var byId = read.Messages.ToDictionary(item => item.MessageId, item => item.Value);
            items = query.Ids.Select(id => byId[id]).ToArray();
        }
        try { return (Root(request, account, query.State!, query.Total, items), null, query.State); }
        catch (XmlException) { return (null, "ErrorInvalidPropertyRequest", query.State); }
        catch (GatewayEwsRequestException exception) { return (null, exception.Code, query.State); }
    }

    private static XElement Root(GatewayEwsRequest request, Guid account, string state, int total, IReadOnlyList<MailMessageSnapshot> items)
    {
        var next = Math.Min(request.Offset, total) + items.Count;
        var root = new XElement(GatewayEwsSoap.Messages + "RootFolder", new XAttribute("TotalItemsInView", total),
            new XAttribute("IncludesLastItemInRange", next == total));
        if (request.Indexed) root.Add(new XAttribute("IndexedPagingOffset", next));
        root.Add(new XElement(GatewayEwsSoap.Types + "Items", items.Select(item => GatewayEwsItemResponse.Message(account, state, item, request.Properties))));
        return root;
    }

    private static XElement Response(XElement? root, string? error)
    {
        var response = new XElement(GatewayEwsSoap.Messages + "FindItemResponseMessage", new XAttribute("ResponseClass", error is null ? "Success" : "Error"));
        if (error is not null) response.Add(new XElement(GatewayEwsSoap.Messages + "MessageText", "The requested mail page is unavailable or unsupported."));
        response.Add(new XElement(GatewayEwsSoap.Messages + "ResponseCode", error ?? "NoError"));
        if (error is null) response.Add(new XElement(root!));
        else response.Add(new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
        return response;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Busy() => throw new GatewayEwsRequestException("ErrorServerBusy", status: StatusCodes.Status503ServiceUnavailable);
}
