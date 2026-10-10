using System.Text;
using System.Xml;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal sealed class GatewayEwsFolderGraph(Guid account, string state, IReadOnlyList<MailFolderSnapshot> folders)
{
    private readonly Dictionary<Guid, MailFolderSnapshot> _folders = folders.ToDictionary(folder => folder.Id);
    private readonly ILookup<Guid?, MailFolderSnapshot> _children = folders.ToLookup(folder => folder.ParentId);
    private readonly string _changeKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(state));

    internal string State => state;
    internal string ChangeKey => _changeKey;
    internal int Count => _folders.Count;
    internal MailFolderSnapshot? Snapshot(Guid id) => _folders.GetValueOrDefault(id);
    internal bool HasChildren(Guid id) => _children[id == Guid.Empty ? null : id].Any();
    internal int ChildCount(Guid id) => _children[id == Guid.Empty ? null : id].Count();
    internal bool HasName(Guid? parentId, string name, Guid? excluding = null) =>
        _children[parentId].Any(folder => folder.Id != excluding && string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Validate(IReadOnlyList<MailFolderSnapshot> folders)
    {
        var ids = new HashSet<Guid>();
        foreach (var folder in folders)
        {
            if (folder is null || folder.Id == Guid.Empty || !ids.Add(folder.Id)
                || string.IsNullOrEmpty(folder.Name) || Encoding.UTF8.GetByteCount(folder.Name) > 255
                || folder.TotalEmails < 0 || folder.UnreadEmails < 0 || folder.UnreadEmails > folder.TotalEmails
                || folder.TotalThreads < 0 || folder.TotalThreads > folder.TotalEmails
                || folder.UnreadThreads < 0 || folder.UnreadThreads > folder.UnreadEmails)
                throw new InvalidOperationException("The EWS folder snapshot is invalid.");
            XmlConvert.VerifyXmlChars(folder.Name);
        }
        var parents = folders.ToDictionary(folder => folder.Id, folder => folder.ParentId);
        foreach (var folder in folders)
        {
            var visited = new HashSet<Guid> { folder.Id };
            var parent = folder.ParentId;
            while (parent is not null)
            {
                if (!visited.Add(parent.Value) || !parents.TryGetValue(parent.Value, out parent))
                    throw new InvalidOperationException("The EWS folder hierarchy is invalid.");
            }
        }
    }

    public (Guid Id, string? Error) Resolve(GatewayEwsFolderReference reference, string username)
    {
        if (!reference.Distinguished)
        {
            if (!GatewayEwsFolderIdCodec.TryDecode(reference.Id, out var requestedAccount, out var folder))
                return (Guid.Empty, "ErrorInvalidIdMalformed");
            if (requestedAccount != account) return (Guid.Empty, "ErrorAccessDenied");
            return folder == Guid.Empty || _folders.ContainsKey(folder)
                ? (folder, null) : (Guid.Empty, "ErrorFolderNotFound");
        }
        if (reference.Mailbox is not null && !string.Equals(reference.Mailbox, username, StringComparison.OrdinalIgnoreCase))
            return (Guid.Empty, "ErrorAccessDenied");
        if (string.Equals(reference.Id, "msgfolderroot", StringComparison.Ordinal)) return (Guid.Empty, null);
        var role = reference.Id switch
        {
            "inbox" => "inbox",
            "sentitems" => "sent",
            "drafts" => "drafts",
            "deleteditems" => "trash",
            "junkemail" => "junk",
            _ => null,
        };
        var matches = role is null ? [] : _folders.Values.Where(folder => string.Equals(folder.Role, role, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? (matches[0].Id, null) : (Guid.Empty, "ErrorFolderNotFound");
    }

    public XElement Render(Guid id, IReadOnlySet<string> properties)
    {
        var folder = id == Guid.Empty ? null : _folders[id];
        var types = GatewayEwsSoap.Types;
        var result = new XElement(types + "Folder",
            new XElement(types + "FolderId", new XAttribute("Id", GatewayEwsFolderIdCodec.Encode(account, id)),
                new XAttribute("ChangeKey", _changeKey)));
        if (id != Guid.Empty && properties.Contains("ParentFolderId"))
            result.Add(new XElement(types + "ParentFolderId",
                new XAttribute("Id", GatewayEwsFolderIdCodec.Encode(account, folder!.ParentId ?? Guid.Empty)),
                new XAttribute("ChangeKey", _changeKey)));
        if (properties.Contains("FolderClass")) result.Add(new XElement(types + "FolderClass", "IPF.Note"));
        if (properties.Contains("DisplayName")) result.Add(new XElement(types + "DisplayName", folder?.Name ?? "Top of Information Store"));
        if (properties.Contains("TotalCount")) result.Add(new XElement(types + "TotalCount", folder?.TotalEmails ?? 0));
        if (properties.Contains("ChildFolderCount")) result.Add(new XElement(types + "ChildFolderCount", ChildCount(id)));
        if (properties.Contains("UnreadCount")) result.Add(new XElement(types + "UnreadCount", folder?.UnreadEmails ?? 0));
        return result;
    }

    public IReadOnlyList<Guid> Find(Guid parent, bool deep, GatewayEwsFolderRestriction? restriction = null)
    {
        var result = new List<MailFolderSnapshot>();
        var pending = new Queue<Guid?>();
        pending.Enqueue(parent == Guid.Empty ? null : parent);
        while (pending.TryDequeue(out var current))
        {
            foreach (var child in _children[current])
            {
                // Traverse the admitted scope independently of the caller predicate:
                // a nonmatching ancestor must not hide a matching deep descendant.
                if (restriction?.Matches(child, ChildCount(child.Id)) ?? true) result.Add(child);
                if (deep) pending.Enqueue(child.Id);
            }
        }
        return result.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.Name, StringComparer.Ordinal).ThenBy(folder => folder.Id)
            .Select(folder => folder.Id).ToArray();
    }
}
