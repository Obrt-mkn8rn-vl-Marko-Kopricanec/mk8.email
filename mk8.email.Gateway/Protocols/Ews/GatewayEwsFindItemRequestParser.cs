using System.Globalization;
using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFindItemRequestParser
{
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "ItemId", "ParentFolderId", "ItemClass", "Subject", "DateTimeReceived", "Size",
        "IsDraft", "DateTimeSent", "HasAttachments", "IsRead",
    };

    public static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation, "Traversal");
        if ((string?)operation.Attribute("Traversal") is not "Shallow")
            throw new GatewayEwsRequestException("ErrorInvalidTraversal");
        var fields = operation.Elements().ToArray();
        if (fields.Length is < 2 or > 3 || fields[0].Name != GatewayEwsSoap.Messages + "ItemShape"
            || fields[^1].Name != GatewayEwsSoap.Messages + "ParentFolderIds") Invalid();
        var properties = GatewayEwsItemRequestParser.Shape(fields[0]);
        // FindItem does not expose GetItem's recipient lists/full Sender/From addresses.
        if (properties.Any(property => !Properties.Contains(property)))
            throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        var (offset, limit, indexed) = fields.Length == 3 ? Page(fields[1])
            : (0, GatewayEwsRequestParser.MaximumReferences, false);
        GatewayEwsRequestParser.Container(fields[^1]);
        var references = fields[^1].Elements().Select(GatewayEwsRequestParser.ParseReference).ToArray();
        if (references.Length == 0) Invalid();
        if (references.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new("FindItem", properties, references, false, offset, limit, indexed);
    }

    private static (int Offset, int Limit, bool Indexed) Page(XElement view)
    {
        if (view.Name != GatewayEwsSoap.Messages + "IndexedPageItemView")
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        GatewayEwsRequestParser.Empty(view, "MaxEntriesReturned", "Offset", "BasePoint");
        if ((string?)view.Attribute("BasePoint") is not "Beginning")
            throw new GatewayEwsRequestException("ErrorInvalidIndexedPagingParameters");
        if (!int.TryParse((string?)view.Attribute("Offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new GatewayEwsRequestException("ErrorInvalidIndexedPagingParameters");
        var limit = GatewayEwsRequestParser.MaximumReferences;
        if (view.Attribute("MaxEntriesReturned") is { } maximum
            && (!int.TryParse(maximum.Value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit <= 0))
            throw new GatewayEwsRequestException("ErrorInvalidIndexedPagingParameters");
        if (limit > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return (offset, limit, true);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new GatewayEwsRequestException("ErrorSchemaValidation");
}
