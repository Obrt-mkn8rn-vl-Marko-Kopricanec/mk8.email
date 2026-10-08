using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCreateParser
{
    internal const int MaximumMimeBytes = 32_768;

    public static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation, "MessageDisposition");
        if ((string?)operation.Attribute("MessageDisposition") is not "SaveOnly")
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var fields = operation.Elements().ToArray();
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Messages + "SavedItemFolderId"
            || fields[1].Name != GatewayEwsSoap.Messages + "Items") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        var folders = fields[0].Elements().ToArray();
        if (folders.Length != 1) throw Invalid();
        if (folders[0].Name != GatewayEwsSoap.Types + "FolderId")
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var destination = GatewayEwsRequestParser.ParseReference(folders[0]);
        if (destination.ChangeKey is not null) throw new GatewayEwsRequestException("ErrorInvalidRequest");
        GatewayEwsRequestParser.Container(fields[1]);
        var items = fields[1].Elements().ToArray();
        if (items.Length == 0) throw Invalid();
        if (items.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new("CreateItem", new HashSet<string>(StringComparer.Ordinal), [destination], false, 0, 0, false,
            MimeCreates: items.Select(Mime).ToArray());
    }

    private static byte[] Mime(XElement item)
    {
        if (item.Name != GatewayEwsSoap.Types + "Message") throw new GatewayEwsRequestException("ErrorInvalidRequest");
        GatewayEwsRequestParser.Container(item);
        var fields = item.Elements().ToArray();
        if (fields.Length != 1 || fields[0].Name != GatewayEwsSoap.Types + "MimeContent")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        var mime = fields[0];
        // CharacterSet is a registered, server-ignored attribute. It cannot
        // reinterpret the native ASCII MIME bytes or grant other properties.
        if (mime.HasElements || mime.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration
            && attribute.Name != XNamespace.None + "CharacterSet")) throw Invalid();
        var text = new string(mime.Value.Where(character => character is not (' ' or '\t' or '\r' or '\n')).ToArray());
        if (text.Length > 4 * ((MaximumMimeBytes + 2) / 3)) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { throw new GatewayEwsRequestException("ErrorInvalidMimeContent"); }
        if (bytes.Length == 0 || bytes.Any(value => value > 127)
            || !string.Equals(text, Convert.ToBase64String(bytes), StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorInvalidMimeContent");
        if (bytes.Length > MaximumMimeBytes) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
        return bytes;
    }

    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
}
