using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderSyncParser
{
    internal static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length is < 1 or > 3 || fields[0].Name != GatewayEwsSoap.Messages + "FolderShape") Invalid();
        var properties = GatewayEwsRequestParser.ParseShape(fields[0]);
        var reference = new GatewayEwsFolderReference("msgfolderroot", true, null, null);
        var scopeSpecified = false;
        var index = 1;
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Messages + "SyncFolderId")
        {
            scopeSpecified = true;
            GatewayEwsRequestParser.Container(fields[index]);
            var roots = fields[index].Elements().ToArray();
            if (roots.Length != 1) Invalid();
            reference = GatewayEwsRequestParser.ParseReference(roots[0]);
            if (reference.ChangeKey is not null) throw new GatewayEwsRequestException("ErrorInvalidRequest");
            index++;
        }
        string? syncState = null;
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Messages + "SyncState")
        {
            GatewayEwsRequestParser.Scalar(fields[index]);
            syncState = fields[index].Value;
            if (!GatewayEwsFolderSyncState.TryDecode(syncState, out _))
                throw new GatewayEwsRequestException("ErrorInvalidSyncStateData");
            index++;
        }
        if (index != fields.Length) Invalid();
        return new("SyncFolderHierarchy", properties, [reference], true, 0, GatewayEwsClient.MaximumGraphSize, false,
            SyncState: syncState, SyncScopeSpecified: scopeSpecified);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new GatewayEwsRequestException("ErrorSchemaValidation");
}
