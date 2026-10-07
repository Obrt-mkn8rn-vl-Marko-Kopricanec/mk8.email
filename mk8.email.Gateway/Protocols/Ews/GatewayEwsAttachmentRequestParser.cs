using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsAttachmentRequestParser
{
    internal static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length is < 1 or > 2 || fields[^1].Name != GatewayEwsSoap.Messages + "AttachmentIds") Invalid();
        if (fields.Length == 2)
        {
            if (fields[0].Name != GatewayEwsSoap.Messages + "AttachmentShape") Invalid();
            GatewayEwsRequestParser.Container(fields[0]);
            // File content is returned by default. Item-body/MIME/extended shapes
            // are not implemented and must not silently alter the capability.
            var options = fields[0].Elements().ToArray();
            if (options.Length > 1) Unsupported();
            if (options.Length == 1)
            {
                if (options[0].Name != GatewayEwsSoap.Types + "IncludeMimeContent") Unsupported();
                GatewayEwsRequestParser.Scalar(options[0]);
                if (options[0].Value is "true" or "1") Unsupported();
                if (options[0].Value is not ("false" or "0")) Invalid();
            }
        }
        GatewayEwsRequestParser.Container(fields[^1]);
        var ids = fields[^1].Elements().Select(ReadId).ToArray();
        if (ids.Length == 0) Invalid();
        if (ids.Length > GatewayEwsRequestParser.MaximumReferences) throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new("GetAttachment", new HashSet<string>(StringComparer.Ordinal), [], false, 0, 0, false, Attachments: ids);
    }

    private static string ReadId(XElement field)
    {
        if (field.Name != GatewayEwsSoap.Types + "AttachmentId") Invalid();
        GatewayEwsRequestParser.Empty(field, "Id");
        var id = (string?)field.Attribute("Id");
        if (string.IsNullOrEmpty(id) || id.Length > 512) Invalid();
        return id;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new GatewayEwsRequestException("ErrorSchemaValidation");
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Unsupported() => throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
}
