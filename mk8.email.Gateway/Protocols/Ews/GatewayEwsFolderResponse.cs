using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderResponse
{
    public static string Render(GatewayEwsRequest request, GatewayEwsFolderGraph? graph, string username, string? error = null)
    {
        var messages = GatewayEwsSoap.Messages;
        var results = new XElement(messages + "ResponseMessages");
        foreach (var reference in request.Folders)
        {
            var (id, code) = graph is null ? (Guid.Empty, error ?? "ErrorFolderNotFound") : graph.Resolve(reference, username);
            var ids = code is null && request.Operation is "FindFolder"
                ? graph!.Find(id, request.Deep, request.FolderRestriction) : [];
            if (code is null && string.Equals(request.Operation, "FindFolder", StringComparison.Ordinal)
                && !request.Indexed && ids.Count > request.Limit)
            {
                code = "ErrorExceededFindCountLimit";
            }
            var response = new XElement(messages + (request.Operation + "ResponseMessage"),
                new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
            if (code is not null)
            {
                response.Add(new XElement(messages + "MessageText", "The requested mail folder is unavailable or unsupported."),
                    new XElement(messages + "ResponseCode", code), new XElement(messages + "DescriptiveLinkKey", 0));
            }
            else
            {
                response.Add(new XElement(messages + "ResponseCode", "NoError"));
                if (string.Equals(request.Operation, "GetFolder", StringComparison.Ordinal))
                    response.Add(new XElement(messages + "Folders", graph!.Render(id, request.Properties)));
                else
                    response.Add(Find(request, graph!, ids));
            }
            results.Add(response);
        }
        return GatewayEwsSoap.Envelope(new XElement(messages + (request.Operation + "Response"), results));
    }

    private static XElement Find(GatewayEwsRequest request, GatewayEwsFolderGraph graph, IReadOnlyList<Guid> ids)
    {
        var offset = Math.Min(request.Offset, ids.Count);
        var page = ids.Skip(offset).Take(request.Limit).ToArray();
        var next = offset + page.Length;
        var root = new XElement(GatewayEwsSoap.Messages + "RootFolder",
            new XAttribute("TotalItemsInView", ids.Count), new XAttribute("IncludesLastItemInRange", next == ids.Count));
        if (request.Indexed) root.Add(new XAttribute("IndexedPagingOffset", next));
        root.Add(new XElement(GatewayEwsSoap.Types + "Folders", page.Select(id => graph.Render(id, request.Properties))));
        return root;
    }
}
