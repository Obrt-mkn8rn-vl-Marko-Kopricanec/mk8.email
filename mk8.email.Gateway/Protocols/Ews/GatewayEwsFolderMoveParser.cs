using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderMoveParser
{
    internal static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Messages + "ToFolderId"
            || fields[1].Name != GatewayEwsSoap.Messages + "FolderIds") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        GatewayEwsRequestParser.Container(fields[1]);
        var destinations = fields[0].Elements().ToArray();
        if (destinations.Length != 1) throw Invalid();
        var destination = GatewayEwsRequestParser.ParseReference(destinations[0]);
        // Destination versions do not describe the sources being relocated.
        if (destination.ChangeKey is not null) throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var sources = fields[1].Elements().Select(GatewayEwsRequestParser.ParseReference).ToArray();
        if (sources.Length == 0) throw Invalid();
        if (sources.Length > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new("MoveFolder", new HashSet<string>(StringComparer.Ordinal), sources, false, 0, 0, false, Destination: destination);
    }

    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
}
