using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemDeleteParser
{
    public static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation, "DeleteType", "SuppressReadReceipts");
        // No recoverable-items store or read-receipt sender is implemented by
        // this presentation. Do not silently substitute deletion semantics.
        if ((string?)operation.Attribute("DeleteType") is not "HardDelete"
            || (string?)operation.Attribute("SuppressReadReceipts") is not ("true" or "1"))
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var fields = operation.Elements().ToArray();
        if (fields.Length != 1 || fields[0].Name != GatewayEwsSoap.Messages + "ItemIds")
            throw new GatewayEwsRequestException("ErrorSchemaValidation");
        GatewayEwsRequestParser.Container(fields[0]);
        var references = fields[0].Elements().Select(GatewayEwsItemRequestParser.Reference).ToArray();
        if (references.Length == 0) throw new GatewayEwsRequestException("ErrorSchemaValidation");
        if (references.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new("DeleteItem", new HashSet<string>(StringComparer.Ordinal), [], false, 0, 0, false, Items: references);
    }
}
