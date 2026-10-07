using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using mk8.email.MailWire;
using mk8.email.Configuration;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsRequestParser
{
    internal const int MaximumReferences = 32;
    internal const int MaximumPageSize = 100;
    private static readonly HashSet<string> SupportedProperties = new(StringComparer.Ordinal)
    {
        "FolderId", "ParentFolderId", "FolderClass", "DisplayName", "TotalCount", "ChildFolderCount", "UnreadCount",
    };
    private static readonly HashSet<string> DistinguishedNames = new(StringComparer.Ordinal)
    {
        "calendar", "contacts", "deleteditems", "drafts", "inbox", "journal", "notes", "outbox", "sentitems", "tasks",
        "msgfolderroot", "root", "junkemail", "searchfolders", "voicemail", "recoverableitemsroot",
        "recoverableitemsdeletions", "recoverableitemsversions", "recoverableitemspurges", "archiveroot",
        "archivemsgfolderroot", "archivedeleteditems", "archiveinbox", "archiverecoverableitemsroot",
        "archiverecoverableitemsdeletions", "archiverecoverableitemsversions", "archiverecoverableitemspurges",
        "syncissues", "conflicts", "localfailures", "serverfailures", "recipientcache", "quickcontacts",
        "conversationhistory", "adminauditlogs", "todosearch", "mycontacts", "directory", "imcontactlist", "peopleconnect", "favorites",
    };

    public static async Task<GatewayEwsRequest> ReadAsync(Stream body, CancellationToken cancellationToken, string? charset = null)
    {
        try
        {
            // SOAP permits UTF-8 and UTF-16. Decode strictly, require UTF-16's BOM,
            // and reconcile both HTTP charset and XML declaration before dispatch.
            var bytes = new byte[GatewayHttpPayloadBudget.SmallRequestBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await body.ReadAsync(bytes.AsMemory(count, Math.Min(16_384, bytes.Length - count)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count == bytes.Length)
                throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded", status: StatusCodes.Status413PayloadTooLarge);
            var kind = count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe ? 1
                : count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff ? 2 : 0;
            var skip = kind != 0 ? 2 : count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            Encoding encoding = kind == 0 ? new UTF8Encoding(false, true) : new UnicodeEncoding(kind == 2, false, true);
            if (!MatchesEncoding(charset, kind)) Invalid();
            using var text = new StringReader(encoding.GetString(bytes, skip, count - skip));
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = GatewayHttpPayloadBudget.SmallRequestBytes,
                IgnoreComments = true,
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            ValidateDocument(document, kind);
            return Parse(document);
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException)
        {
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        }
    }

    private static bool MatchesEncoding(string? name, int kind) => string.IsNullOrEmpty(name)
        || string.Equals(name, kind == 0 ? "utf-8" : "utf-16", StringComparison.OrdinalIgnoreCase)
        || kind != 0 && string.Equals(name, kind == 1 ? "utf-16le" : "utf-16be", StringComparison.OrdinalIgnoreCase);

    private static void ValidateDocument(XDocument document, int kind)
    {
        // SOAP 1.1 forbids PIs anywhere, including document-level nodes and
        // optional headers/scalars whose contents are otherwise not interpreted.
        if (document.DescendantNodes().OfType<XProcessingInstruction>().Any()
            || !MatchesEncoding(document.Declaration?.Encoding, kind)) Invalid();
    }

    private static GatewayEwsRequest Parse(XDocument document)
    {
        var envelope = document.Root;
        if (envelope is null || !string.Equals(envelope.Name.LocalName, "Envelope", StringComparison.Ordinal)) Invalid();
        if (envelope.Name.Namespace != GatewayEwsSoap.Soap)
            throw new GatewayEwsRequestException("VersionMismatch", "s");
        Container(envelope);
        var children = envelope.Elements().ToArray();
        if (children.Length is < 1 or > 2) Invalid();
        var body = children[^1];
        if (body.Name != GatewayEwsSoap.Soap + "Body") Invalid();
        if (children.Length == 2)
        {
            try { ParseHeader(children[0]); }
            catch (GatewayEwsRequestException exception)
            {
                throw new GatewayEwsRequestException(exception.Code, exception.FaultPrefix, exception.Status, exception) { IsHeaderFault = true };
            }
        }
        Container(body);
        var operations = body.Elements().ToArray();
        if (operations.Length != 1 || operations[0].Name.Namespace != GatewayEwsSoap.Messages) Invalid();
        if (operations[0].Name.LocalName is "GetItem") return GatewayEwsItemRequestParser.Parse(operations[0]);
        return operations[0].Name.LocalName is "CreateFolder" or "UpdateFolder" or "DeleteFolder"
            ? GatewayEwsMutationRequestParser.Parse(operations[0]) : ParseFolderRead(operations[0]);
    }

    private static GatewayEwsRequest ParseFolderRead(XElement operation)
    {
        var find = string.Equals(operation.Name.LocalName, "FindFolder", StringComparison.Ordinal);
        if (!find && !string.Equals(operation.Name.LocalName, "GetFolder", StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorInvalidRequest");
        Container(operation, find ? ["Traversal"] : []);
        var traversal = find ? (string?)operation.Attribute("Traversal") : null;
        if (find && traversal is not ("Shallow" or "Deep"))
            throw new GatewayEwsRequestException("ErrorInvalidTraversal");
        var fields = operation.Elements().ToArray();
        if (fields.Length is < 2 or > 3 || !find && fields.Length != 2
            || fields[0].Name != GatewayEwsSoap.Messages + "FolderShape"
            || fields[^1].Name != GatewayEwsSoap.Messages + (find ? "ParentFolderIds" : "FolderIds")) Invalid();
        var properties = ParseShape(fields[0]);
        var (offset, limit, indexed) = fields.Length == 3 ? ParsePage(fields[1]) : (0, MaximumPageSize, false);
        Container(fields[^1]);
        var references = fields[^1].Elements().Select(ParseReference).ToArray();
        if (references.Length == 0) Invalid();
        if (references.Length > MaximumReferences)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        return new(operation.Name.LocalName, properties, references, string.Equals(traversal, "Deep", StringComparison.Ordinal), offset, limit, indexed);
    }

    private static void ParseHeader(XElement header)
    {
        if (header.Name != GatewayEwsSoap.Soap + "Header") Invalid();
        Container(header);
        var versionSeen = false;
        foreach (var entry in header.Elements())
        {
            if (entry.Name.Namespace == XNamespace.None) Invalid();
            var mandatory = (string?)entry.Attribute(GatewayEwsSoap.Soap + "mustUnderstand");
            if (mandatory is not (null or "0" or "1")) Invalid();
            var actor = (string?)entry.Attribute(GatewayEwsSoap.Soap + "actor");
            if (actor is not null && !string.Equals(actor, "http://schemas.xmlsoap.org/soap/actor/next", StringComparison.Ordinal))
                continue;
            if (entry.Name == GatewayEwsSoap.Types + "ExchangeImpersonation")
                throw new GatewayEwsRequestException("ErrorAccessDenied");
            if (entry.Name == GatewayEwsSoap.Types + "RequestServerVersion")
            {
                if (versionSeen) Invalid();
                versionSeen = true;
                Empty(entry, "Version");
                var version = (string?)entry.Attribute("Version");
                if (version is not ("Exchange2007" or "Exchange2007_SP1" or "Exchange2010" or "Exchange2010_SP1"
                    or "Exchange2010_SP2" or "Exchange2013" or "Exchange2013_SP1"))
                    throw new GatewayEwsRequestException("ErrorInvalidServerVersion");
            }
            else if (string.Equals(mandatory, "1", StringComparison.Ordinal))
                throw new GatewayEwsRequestException("MustUnderstand", "s");
        }
    }

    private static HashSet<string> ParseShape(XElement shape)
    {
        Container(shape);
        var fields = shape.Elements().ToArray();
        if (fields.Length is < 1 or > 2 || fields[0].Name != GatewayEwsSoap.Types + "BaseShape"
            || fields.Length == 2 && fields[1].Name != GatewayEwsSoap.Types + "AdditionalProperties") Invalid();
        Scalar(fields[0]);
        var properties = new HashSet<string>(StringComparer.Ordinal) { "FolderId" };
        switch (fields[0].Value)
        {
            case "Default":
                properties.UnionWith(["DisplayName", "TotalCount", "ChildFolderCount", "UnreadCount"]);
                break;
            case "IdOnly": break;
            case "AllProperties": throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            default: Invalid(); break;
        }
        if (fields.Length == 1) return properties;
        Container(fields[1]);
        foreach (var field in fields[1].Elements())
        {
            if (field.Name != GatewayEwsSoap.Types + "FieldURI")
                throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            Empty(field, "FieldURI");
            var uri = (string?)field.Attribute("FieldURI");
            if (uri is null || !uri.StartsWith("folder:", StringComparison.Ordinal)
                || !SupportedProperties.Contains(uri[7..]))
                throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
            properties.Add(uri[7..]);
        }
        return properties;
    }

    private static (int Offset, int Limit, bool Indexed) ParsePage(XElement page)
    {
        if (page.Name != GatewayEwsSoap.Messages + "IndexedPageFolderView")
            throw new GatewayEwsRequestException("ErrorUnsupportedPathForQuery");
        Empty(page, "Offset", "MaxEntriesReturned", "BasePoint");
        if (!string.Equals((string?)page.Attribute("BasePoint"), "Beginning", StringComparison.Ordinal))
            throw new GatewayEwsRequestException("ErrorInvalidIndexedPagingParameters");
        if (!int.TryParse((string?)page.Attribute("Offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new GatewayEwsRequestException("ErrorInvalidIndexedPagingParameters");
        var rawLimit = (string?)page.Attribute("MaxEntriesReturned");
        var limit = MaximumPageSize;
        if (rawLimit is not null && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit == 0))
            throw new GatewayEwsRequestException("ErrorInvalidIndexedPagingParameters");
        return (offset, Math.Min(limit, MaximumPageSize), true);
    }

    internal static GatewayEwsFolderReference ParseReference(XElement reference)
    {
        if (reference.Name == GatewayEwsSoap.Types + "FolderId")
        {
            Empty(reference, "Id", "ChangeKey");
            var id = (string?)reference.Attribute("Id");
            if (string.IsNullOrEmpty(id) || id.Length > 512) Invalid();
            return new(id, false, null, (string?)reference.Attribute("ChangeKey"));
        }
        if (reference.Name != GatewayEwsSoap.Types + "DistinguishedFolderId") Invalid();
        Container(reference, "Id", "ChangeKey");
        var distinguished = (string?)reference.Attribute("Id");
        if (distinguished is null || !DistinguishedNames.Contains(distinguished)) Invalid();
        var mailboxes = reference.Elements().ToArray();
        if (mailboxes.Length > 1) Invalid();
        if (mailboxes.Length == 0) return new(distinguished, true, null, (string?)reference.Attribute("ChangeKey"));
        var mailbox = mailboxes[0];
        if (mailbox.Name != GatewayEwsSoap.Types + "Mailbox") Invalid();
        Container(mailbox);
        var addresses = mailbox.Elements().ToArray();
        if (addresses.Length != 1 || addresses[0].Name != GatewayEwsSoap.Types + "EmailAddress") Invalid();
        var address = addresses[0];
        Scalar(address);
        if (!SmtpAddress.TryNormalize(address.Value, allowEmpty: false, out var normalized)) Invalid();
        return new(distinguished, true, normalized, (string?)reference.Attribute("ChangeKey"));
    }

    internal static void Empty(XElement element, params string[] attributes)
    {
        Container(element, attributes);
        if (element.HasElements) Invalid();
    }

    internal static void Scalar(XElement element)
    {
        if (element.HasElements || element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)) Invalid();
    }

    internal static void Container(XElement element, params string[] attributes)
    {
        if (element.Nodes().Any(node => node is XProcessingInstruction
                || node is XText text && !string.IsNullOrWhiteSpace(text.Value))
            || element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration
                && attribute.Name != GatewayEwsSoap.Soap + "mustUnderstand" && attribute.Name != GatewayEwsSoap.Soap + "actor"
                && (attribute.Name.Namespace != XNamespace.None || !attributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))))
            Invalid();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new GatewayEwsRequestException("ErrorSchemaValidation");
}
