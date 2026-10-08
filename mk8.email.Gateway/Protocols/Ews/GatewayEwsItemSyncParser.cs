using System.Globalization;
using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemSyncParser
{
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "ItemId", "ParentFolderId", "ItemClass", "Subject", "DateTimeReceived", "Size",
        "IsDraft", "DateTimeSent", "HasAttachments", "IsRead",
    };

    internal static GatewayEwsRequest Parse(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length is < 3 or > 6 || fields[0].Name != GatewayEwsSoap.Messages + "ItemShape"
            || fields[1].Name != GatewayEwsSoap.Messages + "SyncFolderId") Invalid();
        var properties = GatewayEwsItemRequestParser.Shape(fields[0], out _);
        if (properties.Any(property => !Properties.Contains(property))) throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        GatewayEwsRequestParser.Container(fields[1]);
        var roots = fields[1].Elements().ToArray();
        if (roots.Length != 1) Invalid();
        var folder = GatewayEwsRequestParser.ParseReference(roots[0]);
        if (folder.ChangeKey is not null) throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var index = 2;
        string? state = null;
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Messages + "SyncState")
        {
            GatewayEwsRequestParser.Scalar(fields[index]);
            state = fields[index++].Value;
            if (!GatewayEwsItemSyncState.TryDecode(state, out _)) throw new GatewayEwsRequestException("ErrorInvalidSyncStateData");
        }
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Messages + "Ignore")
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        if (index == fields.Length || fields[index].Name != GatewayEwsSoap.Messages + "MaxChangesReturned") Invalid();
        GatewayEwsRequestParser.Scalar(fields[index]);
        if (!int.TryParse(fields[index++].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
            || maximum is < 1 or > 512) Invalid();
        if (index < fields.Length && fields[index].Name == GatewayEwsSoap.Messages + "SyncScope")
        {
            GatewayEwsRequestParser.Scalar(fields[index]);
            if (fields[index++].Value is not "NormalItems") throw new GatewayEwsRequestException("ErrorInvalidRequest");
        }
        if (index != fields.Length) Invalid();
        return new("SyncFolderItems", properties, [folder], false, 0, maximum, false, SyncState: state);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new GatewayEwsRequestException("ErrorSchemaValidation");
}
