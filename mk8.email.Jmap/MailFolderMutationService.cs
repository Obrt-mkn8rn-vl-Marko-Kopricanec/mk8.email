using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailFolderMutationService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    MailboxMessageContentService content) : IMailFolderMutationService
{
    private static readonly HashSet<string> JmapMailboxRoles = new HashSet<string>(
        [
            "all", "archive", "drafts", "flagged", "important", "inbox", "junk",
            "memos", "scheduled", "sent", "snoozed", "subscribed", "trash",
        ],
        StringComparer.Ordinal);
    public async Task<MailFolderMutationResult> MutateAsync(
        MailFolderMutationCommand command, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(context.User, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return Empty(MailFolderMutationStatus.AccountNotFound);
        var oldState = await states.GetStateAsync(account.InboxId, JmapConstants.MailboxDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return Empty(MailFolderMutationStatus.StateMismatch);
        var creates = command.Creates.ToDictionary(item => item.CreationId, StringComparer.Ordinal);
        var updates = command.Updates.ToDictionary(item => item.RequestedId, item => item.Patch, StringComparer.Ordinal);
        var destroys = command.Destroys.Select(item => item.RequestedId).ToArray();
        var created = new Dictionary<string, MailFolderCreatedSnapshot>(StringComparer.Ordinal);
        var updated = new HashSet<string>(StringComparer.Ordinal);
        var destroyed = new List<string>();
        var notCreated = new Dictionary<string, MailFolderMutationFailure>(StringComparer.Ordinal);
        var notUpdated = new Dictionary<string, MailFolderMutationFailure>(StringComparer.Ordinal);
        var notDestroyed = new Dictionary<string, MailFolderMutationFailure>(StringComparer.Ordinal);
        var whole = await TryApplyWholeSetAsBatchAsync(account.InboxId, context, creates, updates,
            destroys, command.RemoveEmailsOnDestroy, created, updated, destroyed, cancellationToken).ConfigureAwait(false);
        if (!whole)
        {
            await ApplyCreatesAsync(account.InboxId, context, creates, created, notCreated, cancellationToken)
                .ConfigureAwait(false);
            await ApplyUpdatesAsync(account.InboxId, context, updates, updated, notUpdated, cancellationToken)
                .ConfigureAwait(false);
            await ApplyDestroysAsync(account.InboxId, context, destroys, command.RemoveEmailsOnDestroy,
                destroyed, notDestroyed, cancellationToken).ConfigureAwait(false);
        }
        var newState = await states.GetStateAsync(account.InboxId, JmapConstants.MailboxDataType,
            cancellationToken).ConfigureAwait(false);
        var createOutcomes = command.Creates.Select(item => created.TryGetValue(item.CreationId, out var folder)
            ? new MailFolderCreateOutcome(item.CreationId, folder, null)
            : new MailFolderCreateOutcome(item.CreationId, null, notCreated[item.CreationId])).ToArray();
        var updateOutcomes = command.Updates.Select(item => new MailFolderUpdateOutcome(item.RequestedId,
            updated.Contains(context.ResolveId(item.RequestedId) ?? string.Empty) ? ResolveFolder(item.RequestedId, context) : null,
            notUpdated.GetValueOrDefault(item.RequestedId))).ToArray();
        var destroyOutcomes = destroys.OrderBy(item =>
            destroyed.IndexOf(context.ResolveId(item) ?? string.Empty) is var index && index >= 0 ? index : int.MaxValue)
            .Select(item => new MailFolderDestroyOutcome(item,
            destroyed.Contains(context.ResolveId(item) ?? string.Empty, StringComparer.Ordinal) ? ResolveFolder(item, context) : null,
            notDestroyed.GetValueOrDefault(item))).ToArray();
        return new(MailFolderMutationStatus.Ok, oldState, newState, createOutcomes, updateOutcomes, destroyOutcomes);
    }

    private static Guid? ResolveFolder(string reference, JmapInvocationContext context) =>
        JmapId.TryParseMailbox(context.ResolveId(reference), out var id) ? id : null;

    private static MailFolderMutationResult Empty(MailFolderMutationStatus status) => new(status, null, null, [], [], []);

    private async Task ApplyCreatesAsync(Guid accountId, JmapInvocationContext context,
        Dictionary<string, MailFolderCreate> creates, Dictionary<string, MailFolderCreatedSnapshot> created,
        Dictionary<string, MailFolderMutationFailure> failures, CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, MailFolderCreate>(creates, StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var madeProgress = false;
            foreach (var item in pending.ToArray())
            {
                if (ReferencesPendingParent(item.Value.Values?.ParentReference, pending, context)) continue;
                var result = await CreateAsync(accountId, context, item.Value, cancellationToken).ConfigureAwait(false);
                if (result.Error is not null) failures[item.Key] = result.Error;
                else
                {
                    var folder = result.Folder!;
                    context.CreatedIds[item.Key] = JmapId.Mailbox(folder.Id);
                    created[item.Key] = BuildCreatedResponse(folder.Id, JmapMailboxStore.LeafName(folder.Name),
                        result.ParentId, folder.JmapRole, folder.SortOrder, folder.IsSubscribed);
                }
                pending.Remove(item.Key);
                madeProgress = true;
            }
            if (madeProgress) continue;
            foreach (var item in pending)
                failures[item.Key] = Error(MailFolderMutationError.InvalidProperties, "The parent reference is cyclic.", ["parentId"]);
            pending.Clear();
        }
    }

    private async Task ApplyUpdatesAsync(Guid accountId, JmapInvocationContext context,
        Dictionary<string, MailFolderPatch> updates, HashSet<string> updated,
        Dictionary<string, MailFolderMutationFailure> failures, CancellationToken cancellationToken)
    {
        if (await TryApplyUpdatesAsBatchAsync(accountId, context, updates, updated, cancellationToken).ConfigureAwait(false))
            return;
        foreach (var item in updates)
        {
            var resolved = context.ResolveId(item.Key);
            if (!JmapId.TryParseMailbox(resolved, out var id)) { failures[item.Key] = Error(MailFolderMutationError.NotFound); continue; }
            var error = await UpdateAsync(accountId, id, context, item.Value, cancellationToken).ConfigureAwait(false);
            if (error is null) updated.Add(resolved!);
            else failures[item.Key] = error;
        }
    }

    private async Task ApplyDestroysAsync(Guid accountId, JmapInvocationContext context,
        string[] destroys, bool removeEmails, List<string> destroyed,
        Dictionary<string, MailFolderMutationFailure> failures, CancellationToken cancellationToken)
    {
        var folders = await database.Folders.AsNoTracking().Where(folder => folder.InboxId == accountId)
            .Select(folder => new { folder.Id, folder.Name }).ToDictionaryAsync(folder => folder.Id, cancellationToken)
            .ConfigureAwait(false);
        var pending = new List<(string RequestedId, string ResolvedId, Guid Id, int Depth)>();
        var seen = new HashSet<Guid>();
        foreach (var requested in destroys)
        {
            var resolved = context.ResolveId(requested);
            if (!JmapId.TryParseMailbox(resolved, out var id) || !folders.TryGetValue(id, out var folder))
            { failures[requested] = Error(MailFolderMutationError.NotFound); continue; }
            if (seen.Add(id)) pending.Add((requested, resolved!, id, folder.Name.Count(character => character == '/')));
        }
        // Delete descendants before ancestors, independently of requested order.
        foreach (var item in pending.OrderByDescending(item => item.Depth))
        {
            var error = await DestroyAsync(accountId, item.Id, removeEmails, cancellationToken).ConfigureAwait(false);
            if (error is null) destroyed.Add(item.ResolvedId);
            else failures[item.RequestedId] = error;
        }
        foreach (var requested in destroys)
        {
            if (failures.ContainsKey(requested) || destroyed.Contains(context.ResolveId(requested) ?? string.Empty,
                    StringComparer.Ordinal)) continue;
            var duplicate = pending.First(item => string.Equals(item.ResolvedId, context.ResolveId(requested), StringComparison.Ordinal));
            failures[requested] = failures[duplicate.RequestedId];
        }
    }

    private async Task<CreateResult> CreateAsync(
        Guid accountId,
        JmapInvocationContext context,
        MailFolderCreate value,
        CancellationToken cancellationToken)
    {
        if (value.Failure is not null) return CreateResult.Failed(value.Failure);
        if (!TryResolveValues(value.Values, context, out var plan))
            return CreateResult.Failed(Error(MailFolderMutationError.InvalidProperties));
        var (name, parentId, role, sortOrder, isSubscribed) = plan!;

        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var parent = parentId is null
            ? null
            : folders.SingleOrDefault(folder => folder.Id == parentId.Value);
        if (parentId is not null && parent is null)
            return CreateResult.Failed(Error(MailFolderMutationError.InvalidProperties, properties: ["parentId"]));
        if (role is not null
            && folders.Any(folder => string.Equals(
                JmapMailboxStore.EffectiveRole(folder),
                role,
                StringComparison.Ordinal)))
        {
            return CreateResult.Failed(Error(MailFolderMutationError.InvalidProperties, properties: ["role"]));
        }

        var fullName = parent is null ? name : $"{parent.Name}/{name}";
        if (!IsValidFullName(fullName)
            || folders.Any(folder => string.Equals(folder.Name, fullName, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateResult.Failed(Error(
                MailFolderMutationError.InvalidProperties,
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
        await database.Folders.AddAsync(folder, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CreateResult(folder, parentId, null);
    }

    private async Task<MailFolderMutationFailure?> UpdateAsync(
        Guid accountId,
        Guid folderId,
        JmapInvocationContext context,
        MailFolderPatch patch,
        CancellationToken cancellationToken)
    {
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var folder = folders.SingleOrDefault(candidate => candidate.Id == folderId);
        if (folder is null)
            return Error(MailFolderMutationError.NotFound);

        var parentName = JmapMailboxStore.ParentName(folder.Name);
        var currentParent = parentName is null
            ? null
            : folders.SingleOrDefault(candidate => string.Equals(
                candidate.Name,
                parentName,
                StringComparison.OrdinalIgnoreCase));
        var currentView = (await mailboxes.LoadAsync(accountId, cancellationToken).ConfigureAwait(false))
            .First(candidate => candidate.Id == folderId);
        var failure = ApplyPatch(currentView, patch, context, out var plan);
        if (failure is not null) return failure;
        var (name, parentId, role, sortOrder, isSubscribed) = plan!;

        var currentRole = JmapMailboxStore.EffectiveRole(folder);
        var hierarchyChanged = !string.Equals(
                name,
                JmapMailboxStore.LeafName(folder.Name),
                StringComparison.Ordinal)
            || parentId != currentParent?.Id;
        if (IsProtectedRole(currentRole)
            && (hierarchyChanged || !string.Equals(role, currentRole, StringComparison.Ordinal)))
        {
            return Error(MailFolderMutationError.Forbidden);
        }

        var parent = parentId is null
            ? null
            : folders.SingleOrDefault(candidate => candidate.Id == parentId.Value);
        if (parentId is not null && parent is null)
            return Error(MailFolderMutationError.InvalidProperties, properties: ["parentId"]);
        if (parent?.Id == folder.Id
            || parent is not null && parent.Name.StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase))
        {
            return Error(MailFolderMutationError.InvalidProperties, "Mailbox hierarchy cannot contain a loop.", ["parentId"]);
        }
        if (role is not null
            && folders.Any(candidate => candidate.Id != folder.Id
                && string.Equals(
                    JmapMailboxStore.EffectiveRole(candidate),
                    role,
                    StringComparison.Ordinal)))
        {
            return Error(MailFolderMutationError.InvalidProperties, properties: ["role"]);
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
            return Error(
                MailFolderMutationError.InvalidProperties,
                "The resulting Mailbox hierarchy conflicts with an existing Mailbox or is too long.",
                ["name", "parentId"]);
        }

        foreach (var affectedFolder in affected)
            affectedFolder.Name = renamed[affectedFolder.Id];
        folder.JmapRole = role;
        folder.SuppressDefaultJmapRole = true;
        folder.SortOrder = sortOrder;
        folder.IsSubscribed = isSubscribed;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<bool> TryApplyWholeSetAsBatchAsync(
        Guid accountId,
        JmapInvocationContext context,
        Dictionary<string, MailFolderCreate> creates,
        Dictionary<string, MailFolderPatch> updates,
        string[]? destroys,
        bool onDestroyRemoveEmails,
        Dictionary<string, MailFolderCreatedSnapshot> createdResponse,
        HashSet<string> updatedResponse,
        List<string> destroyedResponse,
        CancellationToken cancellationToken)
    {
        var operationCount = creates.Count + updates.Count + (destroys?.Length ?? 0);
        if (operationCount < 2
            || creates.Count == 0
            && (destroys is null || destroys.Length == 0))
            return false;

        var requestedCreates = creates;

        var views = await mailboxes.LoadAsync(accountId, cancellationToken).ConfigureAwait(false);
        var viewsById = views.ToDictionary(view => view.Id);
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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
            context.Features,
            planningIds)
        {
            ReferenceAliases = context.ReferenceAliases,
        };

        var createPlans = new List<MailboxCreatePlan>(requestedCreates.Count);
        foreach (var item in requestedCreates)
        {
            if (item.Value.Failure is not null || !TryResolveValues(item.Value.Values, planningContext, out var plan))
                return false;
            var (name, parentId, role, sortOrder, isSubscribed) = plan!;

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

                var current = new JmapMailboxView(currentNode.Id, currentNode.Name, currentNode.Name,
                    currentNode.ParentId, currentNode.Role, currentNode.SortOrder, currentNode.IsSubscribed, 0, 0, 0, 0);
                if (ApplyPatch(current, item.Value, planningContext, out var plan) is not null) return false;
                var (name, parentId, role, sortOrder, isSubscribed) = plan!;

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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (destroyedEmails.Count > 0 && !onDestroyRemoveEmails)
            return false;

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational())
            {
                if (database.Database.CurrentTransaction is null)
                    transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var folder in folders.Where(folder => destroyedIds.Contains(folder.Id)
                             || !string.Equals(
                                 folder.Name,
                                 finalNames[folder.Id],
                                 StringComparison.Ordinal)))
                {
                    var temporaryName = $"__jmap_tmp_{folder.Id:N}";
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET name = {temporaryName} WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken).ConfigureAwait(false);
                    // EF's unique-index dependency graph must see the staged database
                    // values, not the pre-swap values, or it rejects a valid swap as a cycle.
                    database.Entry(folder).Property(item => item.Name).OriginalValue = temporaryName;
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
                        cancellationToken).ConfigureAwait(false);
                    database.Entry(folder).Property(item => item.JmapRole).OriginalValue = null;
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
                await database.Folders.AddAsync(new FolderDB
                {
                    Id = node.Id,
                    Name = finalNames[node.Id],
                    InboxId = accountId,
                    JmapRole = node.Role,
                    SuppressDefaultJmapRole = true,
                    SortOrder = node.SortOrder,
                    IsSubscribed = node.IsSubscribed,
                }, cancellationToken).ConfigureAwait(false);
            }
            for (var emailIndex = 0; emailIndex < destroyedEmails.Count; emailIndex++)
                content.DeleteOnCommit(destroyedEmails[emailIndex]);
            if (destroyedEmails.Count > 0)
                database.Emails.RemoveRange(destroyedEmails);
            if (destroyedStoredIds.Length > 0)
            {
                database.Folders.RemoveRange(
                    destroyedStoredIds.Select(id => foldersById[id]));
            }

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }

        for (var planIndex = 0; planIndex < createPlans.Count; planIndex++)
        {
            var plan = createPlans[planIndex];
            var id = JmapId.Mailbox(plan.Node.Id);
            context.CreatedIds[plan.CreationId] = id;
            createdResponse[plan.CreationId] = BuildCreatedResponse(
                plan.Node.Id,
                plan.Node.Name,
                plan.Node.ParentId,
                plan.Node.Role,
                plan.Node.SortOrder,
                plan.Node.IsSubscribed);
        }
        for (var responseIndex = 0; responseIndex < updateResponseIds.Count; responseIndex++)
            updatedResponse.Add(updateResponseIds[responseIndex]);
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
        Dictionary<string, MailFolderPatch> updates,
        HashSet<string> updatedResponse,
        CancellationToken cancellationToken)
    {
        if (updates.Count < 2)
            return false;

        var views = await mailboxes.LoadAsync(accountId, cancellationToken).ConfigureAwait(false);
        var viewsById = views.ToDictionary(view => view.Id);
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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

            if (ApplyPatch(view, item.Value, context, out var plan) is not null) return false;
            var (name, parentId, role, sortOrder, isSubscribed) = plan!;

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
                if (database.Database.CurrentTransaction is null)
                    transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var folder in changedNames)
                {
                    var temporaryName = $"__jmap_tmp_{folder.Id:N}";
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET name = {temporaryName} WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken).ConfigureAwait(false);
                    database.Entry(folder).Property(item => item.Name).OriginalValue = temporaryName;
                }
                foreach (var folder in changedRoles)
                {
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE folders SET jmap_role = NULL WHERE id = {folder.Id} AND inbox_id = {accountId}",
                        cancellationToken).ConfigureAwait(false);
                    database.Entry(folder).Property(item => item.JmapRole).OriginalValue = null;
                }
            }

            for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
                folders[folderIndex].Name = finalNames[folders[folderIndex].Id];
            foreach (var plan in plans)
            {
                var folder = foldersById[plan.Key];
                folder.JmapRole = plan.Value.Role;
                folder.SuppressDefaultJmapRole = true;
                folder.SortOrder = plan.Value.SortOrder;
                folder.IsSubscribed = plan.Value.IsSubscribed;
            }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }

        for (var responseIndex = 0; responseIndex < responseIds.Count; responseIndex++)
            updatedResponse.Add(responseIds[responseIndex]);
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

    private async Task<MailFolderMutationFailure?> DestroyAsync(
        Guid accountId,
        Guid folderId,
        bool onDestroyRemoveEmails,
        CancellationToken cancellationToken)
    {
        var folders = await database.Folders
            .Where(folder => folder.InboxId == accountId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var folder = folders.SingleOrDefault(candidate => candidate.Id == folderId);
        if (folder is null)
            return Error(MailFolderMutationError.NotFound);
        var role = JmapMailboxStore.EffectiveRole(folder);
        if (IsProtectedRole(role))
            return Error(MailFolderMutationError.Forbidden);
        if (folders.Any(candidate => candidate.Id != folder.Id
            && candidate.Name.StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return Error(MailFolderMutationError.MailboxHasChild);
        }

        var messages = await database.Emails
            .Where(email => email.FolderId == folder.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (messages.Count > 0 && !onDestroyRemoveEmails)
            return Error(MailFolderMutationError.MailboxHasEmail);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            content.DeleteOnCommit(messages[messageIndex]);
        if (messages.Count > 0)
            database.Emails.RemoveRange(messages);
        database.Folders.Remove(folder);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static bool ReferencesPendingParent(string? requestedId, Dictionary<string, MailFolderCreate> pending,
        JmapInvocationContext context) =>
        requestedId is not null && context.ResolveId(requestedId) is null
        && context.TryGetReferenceKey(requestedId, out var key) && pending.ContainsKey(key);

    private static bool TryResolveValues(MailFolderValues? value, JmapInvocationContext context,
        out MailboxUpdatePlan? plan)
    {
        plan = null;
        if (value is null || !TryNormalizeName(value.Name, out var name)
            || value.Role is not null && !JmapMailboxRoles.Contains(value.Role)
            || value.SortOrder is < 0 or > int.MaxValue) return false;
        Guid? parentId = null;
        if (value.ParentReference is not null)
        {
            if (!JmapId.TryParseMailbox(context.ResolveId(value.ParentReference), out var id)) return false;
            parentId = id;
        }
        plan = new(name, parentId, value.Role, value.SortOrder, value.IsSubscribed);
        return true;
    }

    private static bool TryNormalizeName(string value, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrEmpty(value) || !JmapJson.ContainsOnlyUnicodeScalars(value)) return false;
        name = value.Normalize(NormalizationForm.FormC);
        return Encoding.UTF8.GetByteCount(name) <= FolderDB.MaximumLeafNameOctets
            && name[0] != '\ufeff' && !name.Contains('/', StringComparison.Ordinal) && !name.Any(char.IsControl)
            && name.EnumerateRunes().All(rune => Rune.GetUnicodeCategory(rune) != UnicodeCategory.OtherNotAssigned);
    }

    private static MailFolderMutationFailure? ApplyPatch(JmapMailboxView current, MailFolderPatch patch,
        JmapInvocationContext context, out MailboxUpdatePlan? plan)
    {
        plan = null;
        if (patch.Failure?.Error == MailFolderMutationError.InvalidPatch) return patch.Failure;
        var invalid = new HashSet<string>(patch.Failure?.Properties ?? [], StringComparer.Ordinal);
        foreach (var expected in patch.Expectations)
        {
            if (!ExpectationMatches(current, expected)) invalid.Add(InvariantName(expected.Field));
        }
        if (invalid.Count > 0)
            return Error(MailFolderMutationError.InvalidProperties, properties: invalid.Order(StringComparer.Ordinal).ToArray());
        if (patch.Values is null) return Error(MailFolderMutationError.InvalidProperties);
        var fields = patch.Fields;
        var draft = patch.Values;
        var value = new MailFolderValues(
            fields.HasFlag(MailFolderFields.Name) ? draft.Name : current.Name,
            fields.HasFlag(MailFolderFields.Parent) ? draft.ParentReference :
                current.ParentId is { } id ? JmapId.Mailbox(id) : null,
            fields.HasFlag(MailFolderFields.Role) ? draft.Role : current.Role,
            fields.HasFlag(MailFolderFields.SortOrder) ? draft.SortOrder : current.SortOrder,
            fields.HasFlag(MailFolderFields.Subscription) ? draft.IsSubscribed : current.IsSubscribed);
        return TryResolveValues(value, context, out plan) ? null : Error(MailFolderMutationError.InvalidProperties);
    }

    private static bool ExpectationMatches(JmapMailboxView current, MailFolderExpectation expected) => expected.Field switch
    {
        MailFolderInvariant.Id => string.Equals(expected.Identifier, JmapId.Mailbox(current.Id), StringComparison.Ordinal),
        MailFolderInvariant.TotalMessages => expected.Count == current.TotalEmails,
        MailFolderInvariant.UnreadMessages => expected.Count == current.UnreadEmails,
        MailFolderInvariant.TotalThreads => expected.Count == current.TotalThreads,
        MailFolderInvariant.UnreadThreads => expected.Count == current.UnreadThreads,
        MailFolderInvariant.Rights => current.IsProtected ? expected.MatchesProtected : expected.MatchesOrdinary,
        _ => throw new InvalidOperationException("Unknown folder invariant."),
    };

    private static string InvariantName(MailFolderInvariant field) => field switch
    {
        MailFolderInvariant.Id => "id",
        MailFolderInvariant.TotalMessages => "totalEmails",
        MailFolderInvariant.UnreadMessages => "unreadEmails",
        MailFolderInvariant.TotalThreads => "totalThreads",
        MailFolderInvariant.UnreadThreads => "unreadThreads",
        MailFolderInvariant.Rights => "myRights",
        _ => throw new InvalidOperationException("Unknown folder invariant."),
    };

    private static MailFolderMutationFailure Error(MailFolderMutationError kind, string? description = null,
        IReadOnlyList<string>? properties = null) => new(kind, description, properties);

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

    private static MailFolderCreatedSnapshot BuildCreatedResponse(Guid id, string name, Guid? parentId,
        string? role, long sortOrder, bool isSubscribed) => new(id, name, parentId, role, sortOrder, isSubscribed);

    private sealed record MailboxUpdatePlan(
        string Name,
        Guid? ParentId,
        string? Role,
        long SortOrder,
        bool IsSubscribed);

    private sealed record MailboxCreatePlan(
        string CreationId,
        MailboxNode Node);

    private sealed record MailboxNode(
        Guid Id,
        string Name,
        Guid? ParentId,
        string? Role,
        long SortOrder,
        bool IsSubscribed);

    private sealed record CreateResult(FolderDB? Folder, Guid? ParentId, MailFolderMutationFailure? Error)
    {
        public static CreateResult Failed(MailFolderMutationFailure error) => new(null, null, error);
    }
}
