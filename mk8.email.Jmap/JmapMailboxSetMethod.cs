using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailboxSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> JmapMailboxRoles = new HashSet<string>(
        [
            "all", "archive", "drafts", "flagged", "important", "inbox", "junk",
            "memos", "scheduled", "sent", "snoozed", "subscribed", "trash",
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> MutableProperties = new HashSet<string>(
        ["name", "parentId", "role", "sortOrder", "isSubscribed"],
        StringComparer.Ordinal);

    public string Name => "Mailbox/set";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "ifInState",
                "create",
                "update",
                "destroy",
                "onDestroyRemoveEmails")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapMethodHelpers.TryGetOptionalBoolean(
                arguments,
                "onDestroyRemoveEmails",
                false,
                out var onDestroyRemoveEmails)
            || !TryGetObjectMap(arguments, "create", out var create)
            || !TryGetObjectMap(arguments, "update", out var update)
            || !TryGetDestroy(arguments, out var destroy)
            || !JmapMethodHelpers.AreValidCreationIds(create?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(update?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(destroy))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }

        var operationCount = (create?.Count ?? 0) + (update?.Count ?? 0) + (destroy?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.MailboxDataType,
            cancellationToken);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var created = new JsonObject();
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();

        var appliedAsWholeSet = await TryApplyWholeSetAsBatchAsync(
            account.InboxId,
            context,
            create,
            update,
            destroy,
            onDestroyRemoveEmails,
            created,
            updated,
            destroyed,
            cancellationToken);

        if (!appliedAsWholeSet && create is not null)
        {
            var pending = new Dictionary<string, JsonObject>(create, StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var madeProgress = false;
                foreach (var item in pending.ToArray())
                {
                    if (!JmapId.IsValidId(item.Key))
                    {
                        notCreated[item.Key] = JmapMethodHelpers.SetError(
                            "invalidProperties",
                            "The creation id is invalid.");
                        pending.Remove(item.Key);
                        madeProgress = true;
                        continue;
                    }
                    if (ReferencesPendingParent(item.Value, pending, context))
                        continue;

                    var result = await CreateAsync(
                        account.InboxId,
                        context,
                        item.Value,
                        cancellationToken);
                    if (result.Error is not null)
                    {
                        notCreated[item.Key] = result.Error;
                    }
                    else
                    {
                        var id = JmapId.Mailbox(result.Folder!.Id);
                        context.CreatedIds[item.Key] = id;
                        created[item.Key] = new JsonObject
                        {
                            ["id"] = id,
                            ["parentId"] = result.ParentId is null
                                ? null
                                : JmapId.Mailbox(result.ParentId.Value),
                            ["role"] = result.Folder.JmapRole,
                            ["sortOrder"] = result.Folder.SortOrder,
                            ["isSubscribed"] = result.Folder.IsSubscribed,
                        };
                    }

                    pending.Remove(item.Key);
                    madeProgress = true;
                }

                if (madeProgress)
                    continue;
                foreach (var item in pending)
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        "The parentId creation reference is cyclic.",
                        ["parentId"]);
                }
                pending.Clear();
            }
        }

        if (!appliedAsWholeSet && update is not null)
        {
            var appliedAsBatch = await TryApplyUpdatesAsBatchAsync(
                account.InboxId,
                context,
                update,
                updated,
                cancellationToken);
            if (!appliedAsBatch)
            {
                foreach (var item in update)
                {
                    var resolvedId = context.ResolveId(item.Key);
                    if (!JmapId.TryParseMailbox(resolvedId, out var folderId))
                    {
                        notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                        continue;
                    }

                    var error = await UpdateAsync(
                        account.InboxId,
                        folderId,
                        context,
                        item.Value,
                        cancellationToken);
                    if (error is null)
                        updated[resolvedId!] = null;
                    else
                        notUpdated[item.Key] = error;
                }
            }
        }

        if (!appliedAsWholeSet && destroy is not null)
        {
            var folders = await database.Folders
                .AsNoTracking()
                .Where(folder => folder.InboxId == account.InboxId)
                .Select(folder => new { folder.Id, folder.Name })
                .ToDictionaryAsync(folder => folder.Id, cancellationToken);
            var pendingDestroys = new List<(string RequestedId, string ResolvedId, Guid Id, int Depth)>();
            var seenIds = new HashSet<Guid>();
            foreach (var requestedId in destroy.Distinct(StringComparer.Ordinal))
            {
                var resolvedId = context.ResolveId(requestedId);
                if (!JmapId.TryParseMailbox(resolvedId, out var folderId))
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }

                if (!folders.TryGetValue(folderId, out var folder))
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                if (!seenIds.Add(folderId))
                    continue;
                pendingDestroys.Add((
                    requestedId,
                    resolvedId!,
                    folderId,
                    folder.Name.Count(character => character == '/')));
            }

            // A /set operation is evaluated by its final valid state. Destroy
            // descendants before ancestors so a parent+child removal does not
            // depend on the order of ids supplied by the client.
            foreach (var pending in pendingDestroys.OrderByDescending(item => item.Depth))
            {
                var error = await DestroyAsync(
                    account.InboxId,
                    pending.Id,
                    onDestroyRemoveEmails,
                    cancellationToken);
                if (error is null)
                    destroyed.Add(pending.ResolvedId);
                else
                    notDestroyed[pending.RequestedId] = error;
            }
        }

        var newState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.MailboxDataType,
            cancellationToken);
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = newState,
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private async Task<CreateResult> CreateAsync(
        Guid accountId,
        JmapInvocationContext context,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        var invalidProperties = value
            .Select(item => item.Key)
            .Where(property => !MutableProperties.Contains(property))
            .ToArray();
        if (invalidProperties.Length > 0)
        {
            return CreateResult.Failed(JmapMethodHelpers.SetError(
                "invalidProperties",
                properties: invalidProperties));
        }

        if (!TryParseName(value, true, out var name)
            || !TryParseParentId(value, context, out var parentId)
            || !TryParseRole(value, out var role)
            || !TryParseSortOrder(value, out var sortOrder)
            || !JmapMethodHelpers.TryGetOptionalBoolean(value, "isSubscribed", true, out var isSubscribed))
        {
            return CreateResult.Failed(JmapMethodHelpers.SetError("invalidProperties"));
        }

        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken);
        var parent = parentId is null
            ? null
            : folders.SingleOrDefault(folder => folder.Id == parentId.Value);
        if (parentId is not null && parent is null)
            return CreateResult.Failed(JmapMethodHelpers.SetError("invalidProperties", properties: ["parentId"]));
        if (role is not null
            && folders.Any(folder => string.Equals(
                JmapMailboxStore.EffectiveRole(folder),
                role,
                StringComparison.Ordinal)))
        {
            return CreateResult.Failed(JmapMethodHelpers.SetError("invalidProperties", properties: ["role"]));
        }

        var fullName = parent is null ? name : $"{parent.Name}/{name}";
        if (!IsValidFullName(fullName)
            || folders.Any(folder => string.Equals(folder.Name, fullName, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateResult.Failed(JmapMethodHelpers.SetError(
                "invalidProperties",
                "A sibling Mailbox already has this name, or the hierarchy is too long.",
                ["name", "parentId"]));
        }

        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = fullName,
            InboxId = accountId,
            JmapRole = role,
            SuppressDefaultJmapRole = true,
            SortOrder = sortOrder,
            IsSubscribed = isSubscribed,
        };
        database.Folders.Add(folder);
        await database.SaveChangesAsync(cancellationToken);
        return new CreateResult(folder, parentId, null);
    }

    private async Task<JsonObject?> UpdateAsync(
        Guid accountId,
        Guid folderId,
        JmapInvocationContext context,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken);
        var folder = folders.SingleOrDefault(candidate => candidate.Id == folderId);
        if (folder is null)
            return JmapMethodHelpers.SetError("notFound");

        var parentName = JmapMailboxStore.ParentName(folder.Name);
        var currentParent = parentName is null
            ? null
            : folders.SingleOrDefault(candidate => string.Equals(
                candidate.Name,
                parentName,
                StringComparison.OrdinalIgnoreCase));
        var currentView = (await mailboxes.LoadAsync(accountId, cancellationToken))
            .Single(candidate => candidate.Id == folderId);
        var current = JmapMailboxJson.Build(currentView);
        if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                current,
                patch,
                MutableProperties,
                out var updated,
                out var invalidProperties))
            return JmapMethodHelpers.SetError("invalidPatch");
        if (invalidProperties.Count > 0)
            return JmapMethodHelpers.SetError("invalidProperties", properties: invalidProperties);
        if (!TryParseName(updated, true, out var name)
            || !TryParseParentId(updated, context, out var parentId)
            || !TryParseRole(updated, out var role)
            || !TryParseSortOrder(updated, out var sortOrder)
            || !JmapMethodHelpers.TryGetOptionalBoolean(updated, "isSubscribed", true, out var isSubscribed))
        {
            return JmapMethodHelpers.SetError("invalidProperties");
        }

        var currentRole = JmapMailboxStore.EffectiveRole(folder);
        var hierarchyChanged = !string.Equals(
                name,
                JmapMailboxStore.LeafName(folder.Name),
                StringComparison.Ordinal)
            || parentId != currentParent?.Id;
        if (IsProtectedRole(currentRole)
            && (hierarchyChanged || !string.Equals(role, currentRole, StringComparison.Ordinal)))
        {
            return JmapMethodHelpers.SetError("forbidden");
        }

        var parent = parentId is null
            ? null
            : folders.SingleOrDefault(candidate => candidate.Id == parentId.Value);
        if (parentId is not null && parent is null)
            return JmapMethodHelpers.SetError("invalidProperties", properties: ["parentId"]);
        if (parent?.Id == folder.Id
            || parent is not null && parent.Name.StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase))
        {
            return JmapMethodHelpers.SetError("invalidProperties", "Mailbox hierarchy cannot contain a loop.", ["parentId"]);
        }
        if (role is not null
            && folders.Any(candidate => candidate.Id != folder.Id
                && string.Equals(
                    JmapMailboxStore.EffectiveRole(candidate),
                    role,
                    StringComparison.Ordinal)))
        {
            return JmapMethodHelpers.SetError("invalidProperties", properties: ["role"]);
        }

        var newFullName = parent is null ? name : $"{parent.Name}/{name}";
        var affected = folders
            .Where(candidate => candidate.Id == folder.Id
                || candidate.Name.StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var affectedIds = affected.Select(candidate => candidate.Id).ToHashSet();
        var renamed = affected.ToDictionary(
            candidate => candidate.Id,
            candidate => newFullName + candidate.Name[folder.Name.Length..]);
        if (renamed.Values.Any(value => !IsValidFullName(value))
            || renamed.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != renamed.Count
            || folders.Any(candidate => !affectedIds.Contains(candidate.Id)
                && renamed.Values.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return JmapMethodHelpers.SetError(
                "invalidProperties",
                "The resulting Mailbox hierarchy conflicts with an existing Mailbox or is too long.",
                ["name", "parentId"]);
        }

        foreach (var affectedFolder in affected)
            affectedFolder.Name = renamed[affectedFolder.Id];
        folder.JmapRole = role;
        folder.SuppressDefaultJmapRole = true;
        folder.SortOrder = sortOrder;
        folder.IsSubscribed = isSubscribed;
        await database.SaveChangesAsync(cancellationToken);
        return null;
    }

    private async Task<bool> TryApplyWholeSetAsBatchAsync(
        Guid accountId,
        JmapInvocationContext context,
        IReadOnlyDictionary<string, JsonObject>? creates,
        IReadOnlyDictionary<string, JsonObject>? updates,
        IReadOnlyList<string>? destroys,
        bool onDestroyRemoveEmails,
        JsonObject createdResponse,
        JsonObject updatedResponse,
        JsonArray destroyedResponse,
        CancellationToken cancellationToken)
    {
        var operationCount = (creates?.Count ?? 0) + (updates?.Count ?? 0) + (destroys?.Count ?? 0);
        if (operationCount < 2
            || (creates is null || creates.Count == 0)
            && (destroys is null || destroys.Count == 0))
            return false;

        IReadOnlyDictionary<string, JsonObject> requestedCreates = creates
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        var views = await mailboxes.LoadAsync(accountId, cancellationToken);
        var viewsById = views.ToDictionary(view => view.Id);
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken);
        var foldersById = folders.ToDictionary(folder => folder.Id);
        var nodes = views.ToDictionary(
            view => view.Id,
            view => new MailboxNode(
                view.Id,
                view.Name,
                view.ParentId,
                view.Role,
                view.SortOrder,
                view.IsSubscribed));

        var planningIds = new Dictionary<string, string>(context.CreatedIds, StringComparer.Ordinal);
        var createIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var item in requestedCreates)
        {
            if (!JmapId.IsValidId(item.Key))
                return false;
            var id = Guid.CreateVersion7();
            createIds[item.Key] = id;
            planningIds[item.Key] = JmapId.Mailbox(id);
        }
        var planningContext = new JmapInvocationContext(
            context.User,
            context.Capabilities,
            planningIds);

        var createPlans = new List<MailboxCreatePlan>(requestedCreates.Count);
        foreach (var item in requestedCreates)
        {
            var value = item.Value;
            if (value.Any(property => !MutableProperties.Contains(property.Key))
                || !TryParseName(value, true, out var name)
                || !TryParseParentId(value, planningContext, out var parentId)
                || !TryParseRole(value, out var role)
                || !TryParseSortOrder(value, out var sortOrder)
                || !JmapMethodHelpers.TryGetOptionalBoolean(
                    value,
                    "isSubscribed",
                    true,
                    out var isSubscribed))
            {
                return false;
            }

            var node = new MailboxNode(
                createIds[item.Key],
                name,
                parentId,
                role,
                sortOrder,
                isSubscribed);
            nodes.Add(node.Id, node);
            createPlans.Add(new MailboxCreatePlan(item.Key, node));
        }

        var explicitlyUpdated = new HashSet<Guid>();
        var updateResponseIds = new List<string>();
        if (updates is not null)
        {
            foreach (var item in updates)
            {
                var resolvedId = planningContext.ResolveId(item.Key);
                if (!JmapId.TryParseMailbox(resolvedId, out var folderId)
                    || !nodes.TryGetValue(folderId, out var currentNode)
                    || !explicitlyUpdated.Add(folderId))
                {
                    return false;
                }

                var current = JmapMailboxJson.Build(new JmapMailboxView(
                    currentNode.Id,
                    currentNode.Name,
                    currentNode.Name,
                    currentNode.ParentId,
                    currentNode.Role,
                    currentNode.SortOrder,
                    currentNode.IsSubscribed,
                    0,
                    0,
                    0,
                    0));
                if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                        current,
                        item.Value,
                        MutableProperties,
                        out var revised,
                        out var invalidProperties)
                    || invalidProperties.Count > 0
                    || !TryParseName(revised, true, out var name)
                    || !TryParseParentId(revised, planningContext, out var parentId)
                    || !TryParseRole(revised, out var role)
                    || !TryParseSortOrder(revised, out var sortOrder)
                    || !JmapMethodHelpers.TryGetOptionalBoolean(
                        revised,
                        "isSubscribed",
                        true,
                        out var isSubscribed))
                {
                    return false;
                }

                if (viewsById.TryGetValue(folderId, out var originalView))
                {
                    var hierarchyChanged = !string.Equals(
                            name,
                            originalView.Name,
                            StringComparison.Ordinal)
                        || parentId != originalView.ParentId;
                    if (originalView.IsProtected
                        && (hierarchyChanged
                            || !string.Equals(role, originalView.Role, StringComparison.Ordinal)))
                    {
                        return false;
                    }
                }

                nodes[folderId] = new MailboxNode(
                    folderId,
                    name,
                    parentId,
                    role,
                    sortOrder,
                    isSubscribed);
                updateResponseIds.Add(resolvedId!);
            }
        }

        var destroyedIds = new HashSet<Guid>();
        var destroyResponseIds = new List<(string Id, Guid FolderId)>();
        if (destroys is not null)
        {
            foreach (var requestedId in destroys)
            {
                var resolvedId = planningContext.ResolveId(requestedId);
                if (!JmapId.TryParseMailbox(resolvedId, out var folderId)
                    || !nodes.ContainsKey(folderId))
                {
                    return false;
                }
                if (!destroyedIds.Add(folderId))
                    continue;
                if (viewsById.TryGetValue(folderId, out var originalView)
                    && originalView.IsProtected)
                {
                    return false;
                }
                destroyResponseIds.Add((resolvedId!, folderId));
            }
        }

        var finalNodes = nodes
            .Where(item => !destroyedIds.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value);
        if (finalNodes.Values.Any(node => node.ParentId is not null
                && !finalNodes.ContainsKey(node.ParentId.Value)))
        {
            return false;
        }

        var finalNames = new Dictionary<Guid, string>();
        var visiting = new HashSet<Guid>();
        foreach (var node in finalNodes.Values)
        {
            if (!TryBuildFullName(node.Id, finalNodes, finalNames, visiting, out _))
                return false;
        }
        if (finalNames.Values.Any(name => !IsValidFullName(name))
            || finalNodes.Values
                .GroupBy(node => node.ParentId)
                .Any(group => group.Select(node => node.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count())
            || finalNodes.Values.Where(node => node.Role is not null)
                .GroupBy(node => node.Role, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
        {
            return false;
        }

        var destroyedStoredIds = destroyedIds.Where(foldersById.ContainsKey).ToArray();
        var destroyedEmails = destroyedStoredIds.Length == 0
            ? []
            : await database.Emails
                .Where(email => destroyedStoredIds.Contains(email.FolderId))
                .ToListAsync(cancellationToken);
        if (destroyedEmails.Count > 0 && !onDestroyRemoveEmails)
            return false;

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational())
            {
                transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                foreach (var folder in folders.Where(folder => destroyedIds.Contains(folder.Id)
                             || !string.Equals(
                                 folder.Name,
                                 finalNames[folder.Id],
                                 StringComparison.Ordinal)))
                {
                    var temporaryName = $"__jmap_tmp_{folder.Id:N}";
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET name = {temporaryName} WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken);
                }
                foreach (var folder in folders.Where(folder => destroyedIds.Contains(folder.Id)
                             || explicitlyUpdated.Contains(folder.Id)
                             && !string.Equals(
                                 folder.JmapRole,
                                 nodes[folder.Id].Role,
                                 StringComparison.Ordinal)))
                {
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET jmap_role = NULL WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken);
                }
            }

            foreach (var folder in folders.Where(folder => !destroyedIds.Contains(folder.Id)))
            {
                folder.Name = finalNames[folder.Id];
                if (!explicitlyUpdated.Contains(folder.Id))
                    continue;
                var node = nodes[folder.Id];
                folder.JmapRole = node.Role;
                folder.SuppressDefaultJmapRole = true;
                folder.SortOrder = node.SortOrder;
                folder.IsSubscribed = node.IsSubscribed;
            }

            foreach (var plan in createPlans.Where(plan => !destroyedIds.Contains(plan.Node.Id)))
            {
                var node = nodes[plan.Node.Id];
                database.Folders.Add(new FolderDB
                {
                    Id = node.Id,
                    Name = finalNames[node.Id],
                    InboxId = accountId,
                    JmapRole = node.Role,
                    SuppressDefaultJmapRole = true,
                    SortOrder = node.SortOrder,
                    IsSubscribed = node.IsSubscribed,
                });
            }
            if (destroyedEmails.Count > 0)
                database.Emails.RemoveRange(destroyedEmails);
            if (destroyedStoredIds.Length > 0)
            {
                database.Folders.RemoveRange(
                    destroyedStoredIds.Select(id => foldersById[id]));
            }

            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }

        foreach (var plan in createPlans)
        {
            var id = JmapId.Mailbox(plan.Node.Id);
            context.CreatedIds[plan.CreationId] = id;
            createdResponse[plan.CreationId] = new JsonObject
            {
                ["id"] = id,
                ["parentId"] = plan.Node.ParentId is null
                    ? null
                    : JmapId.Mailbox(plan.Node.ParentId.Value),
                ["role"] = plan.Node.Role,
                ["sortOrder"] = plan.Node.SortOrder,
                ["isSubscribed"] = plan.Node.IsSubscribed,
            };
        }
        foreach (var responseId in updateResponseIds)
            updatedResponse[responseId] = null;
        foreach (var response in destroyResponseIds
                     .OrderByDescending(item => GetDepth(item.FolderId)))
        {
            destroyedResponse.Add(response.Id);
        }
        return true;

        int GetDepth(Guid id)
        {
            var depth = 0;
            var visited = new HashSet<Guid>();
            while (nodes.TryGetValue(id, out var node)
                && node.ParentId is { } parentId
                && visited.Add(id))
            {
                depth++;
                id = parentId;
            }
            return depth;
        }
    }

    private async Task<bool> TryApplyUpdatesAsBatchAsync(
        Guid accountId,
        JmapInvocationContext context,
        IReadOnlyDictionary<string, JsonObject> updates,
        JsonObject updatedResponse,
        CancellationToken cancellationToken)
    {
        if (updates.Count < 2)
            return false;

        var views = await mailboxes.LoadAsync(accountId, cancellationToken);
        var viewsById = views.ToDictionary(view => view.Id);
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken);
        var foldersById = folders.ToDictionary(folder => folder.Id);
        var plans = new Dictionary<Guid, MailboxUpdatePlan>();
        var responseIds = new List<string>(updates.Count);

        foreach (var item in updates)
        {
            var resolvedId = context.ResolveId(item.Key);
            if (!JmapId.TryParseMailbox(resolvedId, out var folderId)
                || !viewsById.TryGetValue(folderId, out var view)
                || plans.ContainsKey(folderId))
            {
                return false;
            }

            var current = JmapMailboxJson.Build(view);
            if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                    current,
                    item.Value,
                    MutableProperties,
                    out var revised,
                    out var invalidProperties)
                || invalidProperties.Count > 0
                || !TryParseName(revised, true, out var name)
                || !TryParseParentId(revised, context, out var parentId)
                || !TryParseRole(revised, out var role)
                || !TryParseSortOrder(revised, out var sortOrder)
                || !JmapMethodHelpers.TryGetOptionalBoolean(
                    revised,
                    "isSubscribed",
                    true,
                    out var isSubscribed))
            {
                return false;
            }

            var hierarchyChanged = !string.Equals(name, view.Name, StringComparison.Ordinal)
                || parentId != view.ParentId;
            if (view.IsProtected
                && (hierarchyChanged || !string.Equals(role, view.Role, StringComparison.Ordinal)))
            {
                return false;
            }

            plans[folderId] = new MailboxUpdatePlan(
                name,
                parentId,
                role,
                sortOrder,
                isSubscribed);
            responseIds.Add(resolvedId!);
        }

        var nodes = views.ToDictionary(
            view => view.Id,
            view => plans.TryGetValue(view.Id, out var plan)
                ? new MailboxNode(
                    view.Id,
                    plan.Name,
                    plan.ParentId,
                    plan.Role,
                    plan.SortOrder,
                    plan.IsSubscribed)
                : new MailboxNode(
                    view.Id,
                    view.Name,
                    view.ParentId,
                    view.Role,
                    view.SortOrder,
                    view.IsSubscribed));
        if (nodes.Values.Any(node => node.ParentId is not null && !nodes.ContainsKey(node.ParentId.Value)))
            return false;

        var finalNames = new Dictionary<Guid, string>();
        var visiting = new HashSet<Guid>();
        foreach (var node in nodes.Values)
        {
            if (!TryBuildFullName(node.Id, nodes, finalNames, visiting, out _))
                return false;
        }
        if (finalNames.Values.Any(name => !IsValidFullName(name))
            || nodes.Values
                .GroupBy(node => node.ParentId)
                .Any(group => group.Select(node => node.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count())
            || nodes.Values.Where(node => node.Role is not null)
                .GroupBy(node => node.Role, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
        {
            return false;
        }

        var changedNames = folders
            .Where(folder => !string.Equals(
                folder.Name,
                finalNames[folder.Id],
                StringComparison.Ordinal))
            .ToArray();
        var changedRoles = plans
            .Where(item => !string.Equals(
                foldersById[item.Key].JmapRole,
                item.Value.Role,
                StringComparison.Ordinal))
            .Select(item => foldersById[item.Key])
            .ToArray();

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational())
            {
                transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                foreach (var folder in changedNames)
                {
                    var temporaryName = $"__jmap_tmp_{folder.Id:N}";
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET name = {temporaryName} WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken);
                }
                foreach (var folder in changedRoles)
                {
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET jmap_role = NULL WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken);
                }
            }

            foreach (var folder in folders)
                folder.Name = finalNames[folder.Id];
            foreach (var plan in plans)
            {
                var folder = foldersById[plan.Key];
                folder.JmapRole = plan.Value.Role;
                folder.SuppressDefaultJmapRole = true;
                folder.SortOrder = plan.Value.SortOrder;
                folder.IsSubscribed = plan.Value.IsSubscribed;
            }
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }

        foreach (var responseId in responseIds)
            updatedResponse[responseId] = null;
        return true;
    }

    private static bool TryBuildFullName(
        Guid id,
        IReadOnlyDictionary<Guid, MailboxNode> nodes,
        IDictionary<Guid, string> names,
        ISet<Guid> visiting,
        out string fullName)
    {
        if (names.TryGetValue(id, out fullName!))
            return true;
        if (!visiting.Add(id))
        {
            fullName = string.Empty;
            return false;
        }

        var node = nodes[id];
        if (node.ParentId is null)
        {
            fullName = node.Name;
        }
        else if (!TryBuildFullName(
                     node.ParentId.Value,
                     nodes,
                     names,
                     visiting,
                     out var parentName))
        {
            fullName = string.Empty;
            return false;
        }
        else
        {
            fullName = $"{parentName}/{node.Name}";
        }
        visiting.Remove(id);
        names[id] = fullName;
        return true;
    }

    private async Task<JsonObject?> DestroyAsync(
        Guid accountId,
        Guid folderId,
        bool onDestroyRemoveEmails,
        CancellationToken cancellationToken)
    {
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken);
        var folder = folders.SingleOrDefault(candidate => candidate.Id == folderId);
        if (folder is null)
            return JmapMethodHelpers.SetError("notFound");
        var role = JmapMailboxStore.EffectiveRole(folder);
        if (IsProtectedRole(role))
            return JmapMethodHelpers.SetError("forbidden");
        if (folders.Any(candidate => candidate.Id != folder.Id
            && candidate.Name.StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return JmapMethodHelpers.SetError("mailboxHasChild");
        }

        var messages = await database.Emails
            .Where(email => email.FolderId == folder.Id)
            .ToListAsync(cancellationToken);
        if (messages.Count > 0 && !onDestroyRemoveEmails)
            return JmapMethodHelpers.SetError("mailboxHasEmail");
        if (messages.Count > 0)
            database.Emails.RemoveRange(messages);
        database.Folders.Remove(folder);
        await database.SaveChangesAsync(cancellationToken);
        return null;
    }

    private static bool TryGetObjectMap(
        JsonObject arguments,
        string name,
        out IReadOnlyDictionary<string, JsonObject>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonObject map)
            return false;

        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in map)
        {
            if (item.Value is not JsonObject value)
                return false;
            result[item.Key] = value;
        }
        values = result;
        return true;
    }

    private static bool ReferencesPendingParent(
        JsonObject value,
        IReadOnlyDictionary<string, JsonObject> pending,
        JmapInvocationContext context)
    {
        if (value["parentId"] is not JsonValue parentValue
            || !parentValue.TryGetValue<string>(out var requestedId)
            || requestedId is null
            || context.ResolveId(requestedId) is not null
            || requestedId.Length < 2
            || requestedId[0] != '#')
        {
            return false;
        }
        return pending.ContainsKey(requestedId[1..]);
    }

    private static bool TryGetDestroy(
        JsonObject arguments,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var parsed)
                || parsed is null)
            {
                return false;
            }
            result.Add(parsed);
        }
        values = result;
        return true;
    }

    private static bool TryParseName(
        JsonObject value,
        bool required,
        out string name)
    {
        name = string.Empty;
        if (!value.TryGetPropertyValue("name", out var node))
            return !required;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var parsed)
            || string.IsNullOrEmpty(parsed)
            || !JmapJson.ContainsOnlyUnicodeScalars(parsed))
        {
            return false;
        }

        try
        {
            name = parsed.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return Encoding.UTF8.GetByteCount(name) <= FolderDB.MaximumLeafNameOctets
            && IsNetUnicode(name)
            && !name.Contains('/')
            && !name.Any(char.IsControl);
    }

    private static bool IsNetUnicode(string value) =>
        value[0] != '\ufeff'
        && value.EnumerateRunes().All(rune =>
            Rune.GetUnicodeCategory(rune) != UnicodeCategory.OtherNotAssigned);

    private static bool TryParseParentId(
        JsonObject value,
        JmapInvocationContext context,
        out Guid? parentId)
    {
        parentId = null;
        if (!value.TryGetPropertyValue("parentId", out var node) || node is null)
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var requestedId))
        {
            return false;
        }

        var resolvedId = context.ResolveId(requestedId);
        if (!JmapId.TryParseMailbox(resolvedId, out var parsedId))
            return false;
        parentId = parsedId;
        return true;
    }

    private static bool TryParseRole(JsonObject value, out string? role)
    {
        role = null;
        if (!value.TryGetPropertyValue("role", out var node) || node is null)
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out role)
            || !JmapMailboxRoles.Contains(role))
        {
            role = null;
            return false;
        }
        return true;
    }

    private static bool TryParseSortOrder(JsonObject value, out int sortOrder)
    {
        sortOrder = 0;
        if (!value.TryGetPropertyValue("sortOrder", out var node))
            return true;
        if (node is null)
            return false;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out sortOrder)
            && sortOrder >= 0;
    }

    private static bool IsValidFullName(string name)
    {
        if (name.Length is < 1 or > FolderDB.MaximumStoredNameLength)
            return false;
        var parts = name.Split('/');
        return parts.Length <= FolderDB.MaximumHierarchyDepth
            && parts.All(part => part.Length > 0
                && Encoding.UTF8.GetByteCount(part) <= FolderDB.MaximumLeafNameOctets);
    }

    private static bool IsProtectedRole(string? role) =>
        role is "inbox" or "sent" or "drafts" or "trash" or "junk";

    private sealed record MailboxUpdatePlan(
        string Name,
        Guid? ParentId,
        string? Role,
        int SortOrder,
        bool IsSubscribed);

    private sealed record MailboxCreatePlan(
        string CreationId,
        MailboxNode Node);

    private sealed record MailboxNode(
        Guid Id,
        string Name,
        Guid? ParentId,
        string? Role,
        int SortOrder,
        bool IsSubscribed);

    private sealed record CreateResult(FolderDB? Folder, Guid? ParentId, JsonObject? Error)
    {
        public static CreateResult Failed(JsonObject error) => new(null, null, error);
    }
}

internal static class JmapPatchExtensions
{
    public static IEnumerable<string> KeysForPatch(this JsonObject patch)
    {
        foreach (var item in patch)
        {
            var separator = item.Key.IndexOf('/');
            var firstToken = separator < 0 ? item.Key : item.Key[..separator];
            yield return firstToken.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
        }
    }
}
