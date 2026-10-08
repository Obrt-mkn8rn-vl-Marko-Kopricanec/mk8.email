using System.Text;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal sealed record GatewayEwsFolderMovePlan(Guid Id, string? Code)
{
    internal static GatewayEwsFolderMovePlan[] Build(GatewayEwsRequest request, GatewayEwsFolderGraph? graph,
        string username, string unavailable, out Guid destination)
    {
        destination = Guid.Empty;
        var target = request.Destination ?? throw new InvalidOperationException("The EWS move destination is missing.");
        string? targetError = unavailable;
        if (graph is not null) (destination, targetError) = graph.Resolve(target, username);
        var plans = new GatewayEwsFolderMovePlan[request.Folders.Count];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new HashSet<Guid>();
        for (var index = 0; index < plans.Length; index++)
        {
            var source = request.Folders[index];
            var (id, code) = graph is null ? (Guid.Empty, unavailable) : graph.Resolve(source, username);
            code ??= targetError;
            code ??= Validate(graph!, source, id, destination, accepted, names);
            plans[index] = new(id, code);
            if (code is null) accepted.Add(id);
        }
        return plans;
    }

    private static string? Validate(GatewayEwsFolderGraph graph, GatewayEwsFolderReference source, Guid id,
        Guid destination, HashSet<Guid> accepted, HashSet<string> names)
    {
        if (source.Distinguished || id == Guid.Empty || graph.Snapshot(id)!.IsProtected) return "ErrorMoveDistinguishedFolder";
        if (source.ChangeKey is not null)
        {
            var bytes = new byte[1024];
            if (source.ChangeKey.Length > 2048 || !Convert.TryFromBase64String(source.ChangeKey, bytes, out var count)
                || count == 0 || !string.Equals(Convert.ToBase64String(bytes.AsSpan(0, count)), source.ChangeKey, StringComparison.Ordinal))
                return "ErrorInvalidChangeKey";
            if (!string.Equals(source.ChangeKey, graph.ChangeKey, StringComparison.Ordinal)) return "ErrorIrresolvableConflict";
        }
        var descendants = graph.Find(id, deep: true);
        if (id == destination || descendants.Contains(destination)) return "ErrorInvalidRequest";
        // A batch moves disjoint subtrees. Overlap/duplicates refuse the later
        // admitted source instead of giving sequential or order-dependent moves.
        if (accepted.Contains(id) || descendants.Any(accepted.Contains)
            || accepted.Any(parent => graph.Find(parent, deep: true).Contains(id))) return "ErrorInvalidRequest";
        if (descendants.Any(child => graph.Snapshot(child)!.IsProtected)) return "ErrorAccessDenied";
        var folder = graph.Snapshot(id)!;
        if (folder.ParentId == (destination == Guid.Empty ? null : (Guid?)destination)) return "ErrorInvalidRequest";
        if (graph.HasName(destination == Guid.Empty ? null : destination, folder.Name)
            || names.Contains(folder.Name)) return "ErrorFolderExists";
        // Full path length/depth are enforced independently by the Worker. This
        // preflight refuses an unrepresentable subtree without sending a write.
        var prefix = Path(graph, destination);
        foreach (var child in descendants.Prepend(id))
        {
            var suffix = Path(graph, child)[Path(graph, id).Length..];
            var path = (prefix.Length == 0 ? folder.Name : prefix + "/" + folder.Name) + suffix;
            if (path.Length > 5049 || path.Count(character => character == '/') >= 50
                || path.Split('/').Any(part => Encoding.UTF8.GetByteCount(part) > 100)) return "ErrorInvalidFolderName";
        }
        names.Add(folder.Name);
        return null;
    }

    private static string Path(GatewayEwsFolderGraph graph, Guid id)
    {
        var segments = new Stack<string>();
        while (id != Guid.Empty)
        {
            var node = graph.Snapshot(id)!;
            segments.Push(node.Name);
            id = node.ParentId ?? Guid.Empty;
        }
        return string.Join('/', segments);
    }
}
