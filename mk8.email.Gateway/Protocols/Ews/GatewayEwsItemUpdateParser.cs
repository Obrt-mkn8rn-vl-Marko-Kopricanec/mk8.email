using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemUpdateParser
{
    public static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation, "ConflictResolution", "MessageDisposition", "SuppressReadReceipts");
        if ((string?)operation.Attribute("ConflictResolution") is not "NeverOverwrite"
            || (string?)operation.Attribute("MessageDisposition") is not "SaveOnly"
            || (string?)operation.Attribute("SuppressReadReceipts") is not ("true" or "1"))
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var fields = operation.Elements().ToArray();
        if (fields.Length != 1 || fields[0].Name != GatewayEwsSoap.Messages + "ItemChanges") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        var changes = fields[0].Elements().ToArray();
        if (changes.Length == 0) throw Invalid();
        if (changes.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        var parsed = changes.Select(Change).ToArray();
        return new("UpdateItem", new HashSet<string>(StringComparer.Ordinal), [], false, 0, 0, false,
            Items: parsed.Select(item => item.Reference).ToArray(), ReadStates: parsed.Select(item => item.Read).ToArray());
    }

    private static (GatewayEwsItemReference Reference, bool Read) Change(XElement change)
    {
        if (change.Name != GatewayEwsSoap.Types + "ItemChange") throw Invalid();
        GatewayEwsRequestParser.Container(change);
        var fields = change.Elements().ToArray();
        if (fields.Length != 2 || fields[1].Name != GatewayEwsSoap.Types + "Updates") throw Invalid();
        var reference = GatewayEwsItemRequestParser.Reference(fields[0]);
        GatewayEwsRequestParser.Container(fields[1]);
        var updates = fields[1].Elements().ToArray();
        if (updates.Length != 1 || updates[0].Name != GatewayEwsSoap.Types + "SetItemField")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        GatewayEwsRequestParser.Container(updates[0]);
        var values = updates[0].Elements().ToArray();
        if (values.Length != 2 || values[0].Name != GatewayEwsSoap.Types + "FieldURI") throw Invalid();
        GatewayEwsRequestParser.Empty(values[0], "FieldURI");
        if ((string?)values[0].Attribute("FieldURI") is not "message:IsRead" || values[1].Name != GatewayEwsSoap.Types + "Message")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        GatewayEwsRequestParser.Container(values[1]);
        var properties = values[1].Elements().ToArray();
        if (properties.Length != 1 || properties[0].Name != GatewayEwsSoap.Types + "IsRead")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        GatewayEwsRequestParser.Scalar(properties[0]);
        if (properties[0].Value is not ("true" or "1" or "false" or "0")) throw Invalid();
        return (reference, properties[0].Value is "true" or "1");
    }

    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
}
