using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderSync
{
    internal static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        if (account == Guid.Empty) return Error("ErrorFolderNotFound");
        if (request.Folders.Count != 1) throw Invalid();
        var scopeError = ScopeError(request.Folders[0], account, profile.Username);
        if (scopeError is not null) return Error(scopeError);
        GatewayEwsFolderSyncState.Cursor? prior = null;
        if (request.SyncState is not null && (!GatewayEwsFolderSyncState.TryDecode(request.SyncState, out prior)
            || prior!.Account != account || prior.ScopeSpecified != request.SyncScopeSpecified)) return Error("ErrorInvalidSyncStateData");
        var read = await application.ReadGraphAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
        if (read.Status != MailFolderReadStatus.Ok)
            return Error(read.Status == MailFolderReadStatus.RequestTooLarge ? "ErrorExceededFindCountLimit" : "ErrorFolderNotFound");
        if (!GatewayEwsFolderSyncState.TrySequence(read.State, out _)) throw Invalid();
        var graph = new GatewayEwsFolderGraph(account, read.State!, read.Folders);
        var parents = read.Folders.ToDictionary(folder => folder.Id, folder => folder.ParentId);
        XElement changes;
        if (prior is null) changes = Creates(graph, parents.Keys.ToArray(), request.Properties);
        else
        {
            var delta = await application.ReadFolderChangesAsync(authentication, profile, account, prior.State, cancellationToken).ConfigureAwait(false);
            var projection = ProjectDelta(account, graph, parents, prior, delta, request.Properties);
            if (projection.Error is not null) return Error(projection.Error);
            changes = projection.Changes!;
        }
        // Separate operations are independently authorized/atomic, not one
        // ambient HTTP transaction. Withhold a raced composite snapshot.
        var final = await application.ReadGraphAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
        if (final.Status != MailFolderReadStatus.Ok || !string.Equals(final.State, graph.State, StringComparison.Ordinal)) return Error("ErrorServerBusy");
        return Response(new XElement(GatewayEwsSoap.Messages + "ResponseCode", "NoError"),
            new XElement(GatewayEwsSoap.Messages + "SyncState", GatewayEwsFolderSyncState.Encode(account, graph.State, parents, request.SyncScopeSpecified)),
            new XElement(GatewayEwsSoap.Messages + "IncludesLastFolderInRange", true), changes);
    }

    private static (XElement? Changes, string? Error) ProjectDelta(Guid account, GatewayEwsFolderGraph graph,
        IReadOnlyDictionary<Guid, Guid?> parents, GatewayEwsFolderSyncState.Cursor prior, MailChangesResult delta, IReadOnlySet<string> properties)
    {
        if (delta.Status != MailChangesStatus.Ok)
            return (null, delta.Status == MailChangesStatus.AccountNotFound ? "ErrorFolderNotFound" : "ErrorInvalidSyncStateData");
        // A current graph cannot materialize an older partial page faithfully.
        if (delta.HasMoreChanges) return (null, "ErrorExceededFindCountLimit");
        if (!string.Equals(delta.NewState, graph.State, StringComparison.Ordinal)) return (null, "ErrorServerBusy");
        var created = Ids(delta.CreatedKeys);
        var updated = Ids(delta.UpdatedKeys);
        var destroyed = Ids(delta.DestroyedKeys);
        if (created.Concat(updated).Any(id => graph.Snapshot(id) is null) || destroyed.Any(id => graph.Snapshot(id) is not null)) throw Invalid();
        // Prior cache metadata is not authority. Reconcile its complete net set
        // with independently authorized native lifecycle records/current graph.
        if (!ValidMembership(prior.Parents, parents, created, updated, destroyed)) return (null, "ErrorInvalidSyncStateData");
        var changes = Creates(graph, created, properties);
        var updates = new HashSet<Guid>(updated);
        AddDerivedUpdates(prior.Parents, parents, updates);
        foreach (var id in updates.Order())
            changes.Add(new XElement(GatewayEwsSoap.Types + "Update", graph.Render(id, properties)));
        foreach (var id in destroyed.Order())
            changes.Add(new XElement(GatewayEwsSoap.Types + "Delete", new XElement(GatewayEwsSoap.Types + "FolderId",
                new XAttribute("Id", GatewayEwsFolderIdCodec.Encode(account, id)))));
        return (changes, null);
    }

    private static bool ValidMembership(IReadOnlyDictionary<Guid, Guid?> prior, IReadOnlyDictionary<Guid, Guid?> current,
        Guid[] created, Guid[] updated, Guid[] destroyed)
    {
        var expected = new HashSet<Guid>(prior.Keys);
        foreach (var id in created) if (!expected.Add(id)) return false;
        foreach (var id in destroyed) if (!expected.Remove(id)) return false;
        return updated.All(prior.ContainsKey) && expected.SetEquals(current.Keys);
    }

    private static void AddDerivedUpdates(IReadOnlyDictionary<Guid, Guid?> prior, IReadOnlyDictionary<Guid, Guid?> current, HashSet<Guid> updates)
    {
        var previousChildren = prior.Values.ToLookup(parent => parent);
        var currentChildren = current.Values.ToLookup(parent => parent);
        foreach (var folder in current)
        {
            if (prior.TryGetValue(folder.Key, out var oldParent) && (oldParent != folder.Value
                || previousChildren[folder.Key].Count() != currentChildren[folder.Key].Count())) updates.Add(folder.Key);
        }
    }

    private static XElement Creates(GatewayEwsFolderGraph graph, Guid[] ids, IReadOnlySet<string> properties) =>
        new(GatewayEwsSoap.Messages + "Changes", ids.OrderBy(id => Depth(graph, id)).ThenBy(id => id)
            .Select(id => new XElement(GatewayEwsSoap.Types + "Create", graph.Render(id, properties))));

    private static string? ScopeError(GatewayEwsFolderReference root, Guid account, string username)
    {
        if (root.ChangeKey is not null) return "ErrorInvalidRequest";
        if (root.Distinguished)
        {
            if (root.Mailbox is not null && !string.Equals(root.Mailbox, username, StringComparison.OrdinalIgnoreCase)) return "ErrorAccessDenied";
            return root.Id is "msgfolderroot" ? null : "ErrorInvalidRequest";
        }
        if (!GatewayEwsFolderIdCodec.TryDecode(root.Id, out var requestedAccount, out var folder)) return "ErrorInvalidIdMalformed";
        if (requestedAccount != account) return "ErrorAccessDenied";
        return folder == Guid.Empty ? null : "ErrorInvalidRequest";
    }

    private static Guid[] Ids(IReadOnlyList<string> keys) => keys.Select(key => GatewayEwsFolderSyncReply.TryFolder(key, out var id)
        ? id : throw Invalid()).ToArray();

    private static int Depth(GatewayEwsFolderGraph graph, Guid id)
    {
        var depth = 0;
        var parent = graph.Snapshot(id)?.ParentId;
        while (parent is not null)
        {
            depth++;
            parent = graph.Snapshot(parent.Value)!.ParentId;
        }
        return depth;
    }

    private static string Error(string code) => Response(new XElement(GatewayEwsSoap.Messages + "MessageText", "The folder synchronization is unavailable or unsupported."),
        new XElement(GatewayEwsSoap.Messages + "ResponseCode", code), new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));

    private static string Response(params XElement[] fields) => GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "SyncFolderHierarchyResponse",
        new XElement(GatewayEwsSoap.Messages + "ResponseMessages", new XElement(GatewayEwsSoap.Messages + "SyncFolderHierarchyResponseMessage",
            new XAttribute("ResponseClass", fields[0].Name.LocalName is "ResponseCode" ? "Success" : "Error"), fields))));

    private static InvalidOperationException Invalid() => new("Invalid EWS folder synchronization snapshot.");
}
