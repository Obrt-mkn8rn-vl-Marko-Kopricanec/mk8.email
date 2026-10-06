using System.Text;
using System.Xml.Linq;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsMutationRequestParser
{
    public static GatewayEwsRequest Parse(XElement operation) => operation.Name.LocalName switch
    {
        "CreateFolder" => Create(operation),
        "UpdateFolder" => Update(operation),
        "DeleteFolder" => Delete(operation),
        _ => throw Invalid(),
    };

    private static GatewayEwsRequest Create(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Messages + "ParentFolderId"
            || fields[1].Name != GatewayEwsSoap.Messages + "Folders") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        GatewayEwsRequestParser.Container(fields[1]);
        var parents = fields[0].Elements().ToArray();
        if (parents.Length != 1) throw Invalid();
        var parent = GatewayEwsRequestParser.ParseReference(parents[0]);
        var names = fields[1].Elements().Select(folder => ParseName(folder, allowClass: true)).ToArray();
        RequireCount(names.Length);
        return Request("CreateFolder", [parent], names);
    }

    private static GatewayEwsRequest Update(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation);
        var fields = operation.Elements().ToArray();
        if (fields.Length != 1 || fields[0].Name != GatewayEwsSoap.Messages + "FolderChanges") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        var changes = fields[0].Elements().Select(ParseChange).ToArray();
        RequireCount(changes.Length);
        var references = changes.Select(change => change.Reference).ToArray();
        RequireUnique(references);
        return Request("UpdateFolder", references, changes.Select(change => change.Name).ToArray());
    }

    private static GatewayEwsRequest Delete(XElement operation)
    {
        GatewayEwsRequestParser.Container(operation, "DeleteType");
        if (!string.Equals((string?)operation.Attribute("DeleteType"), "HardDelete", StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        var fields = operation.Elements().ToArray();
        if (fields.Length != 1 || fields[0].Name != GatewayEwsSoap.Messages + "FolderIds") throw Invalid();
        GatewayEwsRequestParser.Container(fields[0]);
        var references = fields[0].Elements().Select(GatewayEwsRequestParser.ParseReference).ToArray();
        RequireCount(references.Length);
        RequireUnique(references);
        return Request("DeleteFolder", references, []);
    }

    private static (GatewayEwsFolderReference Reference, string Name) ParseChange(XElement element)
    {
        if (element.Name != GatewayEwsSoap.Types + "FolderChange") throw Invalid();
        GatewayEwsRequestParser.Container(element);
        var fields = element.Elements().ToArray();
        if (fields.Length != 2 || fields[1].Name != GatewayEwsSoap.Types + "Updates") throw Invalid();
        var reference = GatewayEwsRequestParser.ParseReference(fields[0]);
        GatewayEwsRequestParser.Container(fields[1]);
        var updates = fields[1].Elements().ToArray();
        if (updates.Length != 1 || updates[0].Name != GatewayEwsSoap.Types + "SetFolderField")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        GatewayEwsRequestParser.Container(updates[0]);
        var values = updates[0].Elements().ToArray();
        if (values.Length != 2 || values[0].Name != GatewayEwsSoap.Types + "FieldURI") throw Invalid();
        GatewayEwsRequestParser.Empty(values[0], "FieldURI");
        if (!string.Equals((string?)values[0].Attribute("FieldURI"), "folder:DisplayName", StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        return (reference, ParseName(values[1], allowClass: false));
    }

    private static string ParseName(XElement folder, bool allowClass)
    {
        if (folder.Name != GatewayEwsSoap.Types + "Folder")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        GatewayEwsRequestParser.Container(folder);
        var fields = folder.Elements().ToArray();
        if (allowClass && fields.Length == 2 && fields[0].Name == GatewayEwsSoap.Types + "FolderClass")
        {
            GatewayEwsRequestParser.Scalar(fields[0]);
            if (!string.Equals(fields[0].Value, "IPF.Note", StringComparison.Ordinal))
                throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
            fields = fields[1..];
        }
        if (fields.Length != 1 || fields[0].Name != GatewayEwsSoap.Types + "DisplayName")
            throw new GatewayEwsRequestException("ErrorInvalidPropertySet");
        GatewayEwsRequestParser.Scalar(fields[0]);
        var name = fields[0].Value;
        if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 255
            || name.Contains('/', StringComparison.Ordinal) || name.Any(char.IsControl))
            throw new GatewayEwsRequestException("ErrorInvalidFolderName");
        return name;
    }

    private static void RequireCount(int count)
    {
        if (count == 0) throw Invalid();
        if (count > GatewayEwsRequestParser.MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
    }

    private static void RequireUnique(GatewayEwsFolderReference[] references)
    {
        if (references.Select(reference => (reference.Id, reference.Distinguished, reference.Mailbox))
            .Distinct().Count() != references.Length) throw new GatewayEwsRequestException("ErrorInvalidRequest");
    }

    private static GatewayEwsRequest Request(string operation, IReadOnlyList<GatewayEwsFolderReference> references,
        IReadOnlyList<string> names) => new(operation, new HashSet<string>(StringComparer.Ordinal), references, false, 0, 0, false, names);

    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
}
