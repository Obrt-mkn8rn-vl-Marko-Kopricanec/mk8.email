using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCopyParser
{
    public static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length is < 2 or > 3 || fields[0].Name != GatewayEwsSoap.Messages + "ToFolderId"
            || fields[1].Name != GatewayEwsSoap.Messages + "ItemIds") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        var folders = fields[0].Elements().ToArray();
        if (folders.Length != 1) throw Invalid();
        // These initial transfer profiles bind an ordinary physical folder identity.
        // It does not silently ignore a destination version or resolve a role
        // outside the copy transaction without a corresponding domain guard.
        if (folders[0].Name != GatewayEwsSoap.Types + "FolderId")
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var destination = GatewayEwsRequestParser.ParseReference(folders[0]);
        if (destination.ChangeKey is not null) throw new GatewayEwsRequestException("ErrorInvalidRequest");
        GatewayEwsRequestParser.Container(fields[1]);
        var items = fields[1].Elements().Select(GatewayEwsItemRequestParser.Reference).ToArray();
        if (items.Length == 0) throw Invalid();
        if (items.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        var returnIds = true;
        if (fields.Length == 3)
        {
            if (fields[2].Name != GatewayEwsSoap.Messages + "ReturnNewItemIds") throw Invalid();
            GatewayEwsRequestParser.Scalar(fields[2]);
            if (fields[2].Value is not ("true" or "1" or "false" or "0")) throw Invalid();
            returnIds = fields[2].Value is "true" or "1";
        }
        return new(operation.Name.LocalName, new HashSet<string>(StringComparer.Ordinal), [destination], false, 0, 0, false,
            Items: items, ReturnNewItemIds: returnIds);
    }

    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
}
