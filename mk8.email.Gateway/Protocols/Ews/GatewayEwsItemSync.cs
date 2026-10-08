using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemSync
{
    internal static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, GatewayEwsFolderGraph? graph,
        string graphError, CancellationToken cancellationToken)
    {
        if (graph is null) return Error(graphError);
        if (request.Folders.Count != 1) throw Invalid();
        var (folder, error) = graph.Resolve(request.Folders[0], profile.Username);
        if (error is not null || folder == Guid.Empty) return Error(error ?? "ErrorInvalidRequest");
        var shape = GatewayEwsItemSyncState.Shape(request.Properties);
        GatewayEwsItemSyncState.Cursor? prior = null;
        if (request.SyncState is not null && (!GatewayEwsItemSyncState.TryDecode(request.SyncState, out prior)
            || prior!.Account != account || prior.Folder != folder || !prior.Shape.AsSpan().SequenceEqual(shape)))
            return Error("ErrorInvalidSyncStateData");
        var query = await application.QueryItemsAsync(authentication, profile, account, folder, 0,
            Math.Min(GatewayEwsRequestParser.MaximumReferences, profile.Limits.MaxObjectsInGet), cancellationToken).ConfigureAwait(false);
        if (query.Status != MailMessageQueryStatus.Ok) return Error("ErrorFolderNotFound");
        if (query.Total > query.Ids.Count) return Error("ErrorExceededFindCountLimit");
        if (!GatewayEwsFolderSyncState.TrySequence(query.State, out _)) throw Invalid();
        var items = await ReadAsync(application, authentication, profile, account, folder, query, cancellationToken).ConfigureAwait(false);
        var current = items.ToDictionary(item => item.MessageId, item => item.Value.Stored!.Keywords.Contains("$seen", StringComparer.Ordinal));
        XElement changes;
        if (prior is null) changes = new(GatewayEwsSoap.Messages + "Changes", items.Select(item => Item("Create", account, query.State!, item.Value, request.Properties)));
        else
        {
            var delta = await application.ReadItemChangesAsync(authentication, profile, account, prior.State, cancellationToken).ConfigureAwait(false);
            var projection = Project(account, query.State!, prior, items, current, delta, request.Properties);
            if (projection.Error is not null) return Error(projection.Error);
            changes = projection.Changes!;
        }
        if (changes.Elements().Count() > request.Limit) return Error("ErrorExceededFindCountLimit");
        var finalState = await application.ReadMessageStateAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
        var finalGraph = await application.ReadGraphAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(finalState, query.State, StringComparison.Ordinal) || finalGraph.Status != MailFolderReadStatus.Ok
            || !string.Equals(finalGraph.State, graph.State, StringComparison.Ordinal)) return Error("ErrorServerBusy");
        return Response(new XElement(GatewayEwsSoap.Messages + "ResponseCode", "NoError"),
            new XElement(GatewayEwsSoap.Messages + "SyncState", GatewayEwsItemSyncState.Encode(account, folder, query.State!, shape, current)),
            new XElement(GatewayEwsSoap.Messages + "IncludesLastItemInRange", true), changes);
    }

    private static async Task<IReadOnlyList<MailMessageProjectedItem>> ReadAsync(GatewayEwsClient application,
        ProtocolAuthentication authentication, JmapApplicationProfile profile, Guid account, Guid folder,
        MailMessageQueryResult query, CancellationToken cancellationToken)
    {
        if (query.Ids.Count == 0) return [];
        var read = await application.ReadItemsAsync(authentication, profile, account, query.Ids, cancellationToken).ConfigureAwait(false);
        if (read.Status == MailMessageReadStatus.RequestTooLarge) throw new GatewayEwsRequestException("ErrorDataSizeLimitExceeded");
        if (read.Status != MailMessageReadStatus.Ok || read.Messages.Count != query.Ids.Count
            || !string.Equals(read.State, query.State, StringComparison.Ordinal) || read.Messages.Any(item => item.Value.Stored!.FolderId != folder))
            throw new GatewayEwsRequestException("ErrorServerBusy", status: StatusCodes.Status503ServiceUnavailable);
        var byId = read.Messages.ToDictionary(item => item.MessageId);
        return query.Ids.Select(id => byId[id]).ToArray();
    }

    internal static (XElement? Changes, string? Error) Project(Guid account, string state, GatewayEwsItemSyncState.Cursor prior,
        IReadOnlyList<MailMessageProjectedItem> items, IReadOnlyDictionary<Guid, bool> current, MailChangesResult delta,
        IReadOnlySet<string> properties)
    {
        if (delta.Status != MailChangesStatus.Ok) return (null, delta.Status == MailChangesStatus.AccountNotFound ? "ErrorFolderNotFound" : "ErrorInvalidSyncStateData");
        if (delta.HasMoreChanges) return (null, "ErrorExceededFindCountLimit");
        if (!string.Equals(delta.NewState, state, StringComparison.Ordinal)) return (null, "ErrorServerBusy");
        var created = Ids(delta.CreatedKeys);
        var updated = Ids(delta.UpdatedKeys);
        var destroyed = Ids(delta.DestroyedKeys);
        var changed = created.Concat(updated).Concat(destroyed).ToHashSet();
        if (destroyed.Any(current.ContainsKey)) throw Invalid();
        // Folder moves are native updates, not account-wide creates/deletes.
        // Unjournaled net membership/read-flag changes cannot advance a cache.
        if (current.Keys.Except(prior.ReadStates.Keys).Any(id => !created.Contains(id) && !updated.Contains(id))
            || prior.ReadStates.Keys.Except(current.Keys).Any(id => !destroyed.Contains(id) && !updated.Contains(id))
            || created.Any(prior.ReadStates.ContainsKey)
            || current.Any(entry => prior.ReadStates.TryGetValue(entry.Key, out var seen) && seen != entry.Value && !changed.Contains(entry.Key)))
            return (null, "ErrorInvalidSyncStateData");
        var changes = new XElement(GatewayEwsSoap.Messages + "Changes");
        foreach (var item in items)
        {
            var id = item.MessageId;
            if (!prior.ReadStates.ContainsKey(id)) changes.Add(Item("Create", account, state, item.Value, properties));
            else if (updated.Contains(id))
            {
                // Native changes do not name individual scalar fields. Resend the
                // admitted projection, plus a registered read-flag event if needed.
                changes.Add(Item("Update", account, state, item.Value, properties));
                if (prior.ReadStates[id] != current[id]) changes.Add(new XElement(GatewayEwsSoap.Types + "ReadFlagChange",
                    new XElement(GatewayEwsSoap.Types + "ItemId", new XAttribute("Id", GatewayEwsItemIdCodec.Encode(account, id))),
                    new XElement(GatewayEwsSoap.Types + "IsRead", current[id])));
            }
        }
        foreach (var id in prior.ReadStates.Keys.Except(current.Keys).Order())
            changes.Add(new XElement(GatewayEwsSoap.Types + "Delete", new XElement(GatewayEwsSoap.Types + "ItemId",
                new XAttribute("Id", GatewayEwsItemIdCodec.Encode(account, id)))));
        return (changes, null);
    }

    private static XElement Item(string kind, Guid account, string state, MailMessageSnapshot snapshot, IReadOnlySet<string> properties) =>
        new(GatewayEwsSoap.Types + kind, GatewayEwsItemResponse.Message(account, state, snapshot, properties));
    private static Guid[] Ids(IReadOnlyList<string> values) => values.Select(value => GatewayEwsItemSyncReply.TryItem(value, out var id) ? id : throw Invalid()).ToArray();
    private static string Error(string code) => Response(new XElement(GatewayEwsSoap.Messages + "MessageText", "The item synchronization is unavailable or unsupported."),
        new XElement(GatewayEwsSoap.Messages + "ResponseCode", code), new XElement(GatewayEwsSoap.Messages + "DescriptiveLinkKey", 0));
    private static string Response(params XElement[] fields) => GatewayEwsSoap.Envelope(new XElement(GatewayEwsSoap.Messages + "SyncFolderItemsResponse",
        new XElement(GatewayEwsSoap.Messages + "ResponseMessages", new XElement(GatewayEwsSoap.Messages + "SyncFolderItemsResponseMessage",
            new XAttribute("ResponseClass", fields[0].Name.LocalName is "ResponseCode" ? "Success" : "Error"), fields))));
    private static InvalidOperationException Invalid() => new("Invalid EWS item synchronization snapshot.");
}
