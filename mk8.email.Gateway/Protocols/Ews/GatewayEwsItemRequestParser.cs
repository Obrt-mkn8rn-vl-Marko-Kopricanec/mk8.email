using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemRequestParser
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "item:ItemId", "item:ParentFolderId", "item:ItemClass", "item:Subject", "item:DateTimeReceived", "item:Size",
        "item:IsDraft", "item:DateTimeSent", "item:HasAttachments", "item:Body", "item:Attachments", "message:Sender", "message:ToRecipients",
        "message:CcRecipients", "message:BccRecipients", "message:From", "message:InternetMessageId", "message:IsRead", "message:ReplyTo",
    };

    public static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Messages + "ItemShape"
            || fields[1].Name != GatewayEwsSoap.Messages + "ItemIds") Invalid();
        var properties = Shape(fields[0], out var bodyType, allowBody: true);
        GatewayEwsRequestParser.Container(fields[1]);
        var references = fields[1].Elements().Select(Reference).ToArray();
        if (references.Length == 0) Invalid();
        if (references.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new("GetItem", properties, [], false, 0, 0, false, Items: references, BodyType: bodyType);
    }

    internal static HashSet<string> Shape(XElement shape, out string bodyType, bool allowBody = false)
    {
        bodyType = "Best";
        GatewayEwsRequestParser.Container(shape);
        var fields = shape.Elements().ToArray();
        if (fields.Length == 0 || fields[0].Name != GatewayEwsSoap.Types + "BaseShape") Invalid();
        GatewayEwsRequestParser.Scalar(fields[0]);
        if (fields[0].Value is "Default" or "AllProperties") throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        if (fields[0].Value is not "IdOnly") Invalid();
        var properties = new HashSet<string>(StringComparer.Ordinal) { "ItemId" };
        var index = 1;
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Types + "IncludeMimeContent")
        {
            GatewayEwsRequestParser.Scalar(fields[index]);
            if (fields[index].Value is "true" or "1")
            {
                if (!allowBody) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
                properties.Add("MimeContent");
            }
            else if (fields[index].Value is not ("false" or "0")) Invalid();
            index++;
        }
        (index, bodyType) = BodyOptions(fields, index, allowBody);
        if (index == fields.Length) return properties;
        if (index != fields.Length - 1 || fields[index].Name != GatewayEwsSoap.Types + "AdditionalProperties")
            throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        GatewayEwsRequestParser.Container(fields[index]);
        foreach (var field in fields[index].Elements())
        {
            if (field.Name != GatewayEwsSoap.Types + "FieldURI") throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            GatewayEwsRequestParser.Empty(field, "FieldURI");
            var uri = (string?)field.Attribute("FieldURI");
            if (uri is null || !Fields.Contains(uri) || uri is "item:Body" or "item:Attachments" && !allowBody)
                throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            properties.Add(uri[(uri.IndexOf(':', StringComparison.Ordinal) + 1)..]);
        }
        return properties;
    }

    private static (int Index, string Type) BodyOptions(XElement[] fields, int index, bool allowBody)
    {
        var type = "Best";
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Types + "BodyType")
        {
            GatewayEwsRequestParser.Scalar(fields[index]);
            type = fields[index++].Value;
            if (type is not ("Best" or "Text" or "HTML")) Invalid();
            if (!allowBody) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        }
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Types + "FilterHtmlContent")
        {
            GatewayEwsRequestParser.Scalar(fields[index]);
            if (fields[index].Value is "true" or "1") throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            if (fields[index++].Value is not ("false" or "0")) Invalid();
            if (!allowBody) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        }
        return (index, type);
    }

    private static GatewayEwsItemReference Reference(XElement reference)
    {
        if (reference.Name != GatewayEwsSoap.Types + "ItemId") Invalid();
        GatewayEwsRequestParser.Empty(reference, "Id", "ChangeKey");
        var id = (string?)reference.Attribute("Id");
        var key = (string?)reference.Attribute("ChangeKey");
        if (string.IsNullOrEmpty(id) || id.Length > 512 || key?.Length > 2048) Invalid();
        return new(id, key);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new GatewayEwsRequestException("ErrorSchemaValidation");
}
