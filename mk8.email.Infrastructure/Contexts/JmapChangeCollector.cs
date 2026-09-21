using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Data;

internal static class JmapChangeCollector
{
    private const string MailboxType = "Mailbox";
    private const string ThreadType = "Thread";
    private const string EmailType = "Email";
    private const string EmailDeliveryType = "EmailDelivery";
    private const string IdentityType = "Identity";
    private const string SubmissionType = "EmailSubmission";
    private const string VacationType = "VacationResponse";
    private const string AddressBookType = "AddressBook";
    private const string ContactCardType = "ContactCard";
    private const string Created = "created";
    private const string Updated = "updated";
    private const string Destroyed = "destroyed";

    public static async Task CollectAsync(
        EmailDbContext database,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.DetectChanges();
        var emailEntries = database.ChangeTracker.Entries<EmailDB>()
            .Where(IsChanged)
            .ToList();
        var folderEntries = database.ChangeTracker.Entries<FolderDB>()
            .Where(IsChanged)
            .ToList();
        var inboxEntries = database.ChangeTracker.Entries<InboxDB>()
            .Where(IsChanged)
            .ToList();
        var submissionEntries = database.ChangeTracker.Entries<JmapEmailSubmissionDB>()
            .Where(IsChanged)
            .ToList();
        var vacationEntries = database.ChangeTracker.Entries<JmapVacationResponseDB>()
            .Where(IsChanged)
            .ToList();
        var identityEntries = database.ChangeTracker.Entries<JmapIdentityDB>()
            .Where(IsChanged)
            .ToList();
        var davCollectionEntries = database.ChangeTracker.Entries<DavCollectionDB>()
            .Where(IsChanged)
            .ToList();
        var davResourceEntries = database.ChangeTracker.Entries<DavResourceDB>()
            .Where(IsChanged)
            .ToList();
        var changedQueueIds = database.ChangeTracker.Entries<MailQueueMessageDB>()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        changedQueueIds.UnionWith(database.ChangeTracker.Entries<MailQueueRecipientDB>()
            .Where(IsChanged)
            .Select(entry => entry.Entity.MessageId));

        if (emailEntries.Count == 0
            && folderEntries.Count == 0
            && inboxEntries.Count == 0
            && submissionEntries.Count == 0
            && vacationEntries.Count == 0
            && identityEntries.Count == 0
            && davCollectionEntries.Count == 0
            && davResourceEntries.Count == 0
            && changedQueueIds.Count == 0)
        {
            return;
        }

        AssignDefaultMailboxRoles(folderEntries);

        var persistedEmailIds = emailEntries
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .Distinct()
            .ToArray();
        var persistedEmails = persistedEmailIds.Length == 0
            ? new Dictionary<Guid, PersistedEmail>()
            : await database.Emails
                .AsNoTracking()
                .Where(email => persistedEmailIds.Contains(email.Id))
                .Select(email => new PersistedEmail(
                    email.Id,
                    email.FolderId,
                    email.ThreadObjectId,
                    email.IsDeleted))
                .ToDictionaryAsync(email => email.Id, cancellationToken);

        var folderIds = new HashSet<Guid>();
        foreach (var entry in emailEntries)
        {
            if (entry.State == EntityState.Added
                || entry.Property(email => email.FolderId).IsModified)
            {
                folderIds.Add(entry.Entity.FolderId);
            }
            if (persistedEmails.TryGetValue(entry.Entity.Id, out var persisted))
                folderIds.Add(persisted.FolderId);
        }
        foreach (var entry in folderEntries)
            folderIds.Add(entry.Entity.Id);

        var folderAccounts = folderIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await database.Folders
                .AsNoTracking()
                .Where(folder => folderIds.Contains(folder.Id))
                .Select(folder => new { folder.Id, folder.InboxId })
                .ToDictionaryAsync(folder => folder.Id, folder => folder.InboxId, cancellationToken);
        foreach (var entry in folderEntries)
        {
            if (entry.State != EntityState.Deleted)
                folderAccounts[entry.Entity.Id] = entry.Entity.InboxId;
        }

        var trackedChangedEmailIds = emailEntries
            .Select(entry => entry.Entity.Id)
            .ToArray();
        var deletedFolderIds = folderEntries
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToArray();
        var cascadeDeletedEmails = deletedFolderIds.Length == 0
            ? []
            : await database.Emails
                .AsNoTracking()
                .Where(email => deletedFolderIds.Contains(email.FolderId)
                    && !trackedChangedEmailIds.Contains(email.Id)
                    && !email.IsDeleted)
                .Select(email => new PersistedEmail(
                    email.Id,
                    email.FolderId,
                    email.ThreadObjectId,
                    email.IsDeleted))
                .ToListAsync(cancellationToken);

        var persistedDavResourceIds = davResourceEntries
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .Distinct()
            .ToArray();
        var persistedDavResources = persistedDavResourceIds.Length == 0
            ? new Dictionary<Guid, PersistedDavResource>()
            : await database.DavResources
                .AsNoTracking()
                .Where(resource => persistedDavResourceIds.Contains(resource.Id))
                .Select(resource => new PersistedDavResource(resource.Id, resource.CollectionId))
                .ToDictionaryAsync(resource => resource.Id, cancellationToken);

        var davCollectionIds = davCollectionEntries.Select(entry => entry.Entity.Id).ToHashSet();
        foreach (var entry in davResourceEntries)
        {
            davCollectionIds.Add(entry.Entity.CollectionId);
            if (persistedDavResources.TryGetValue(entry.Entity.Id, out var persistedResource))
                davCollectionIds.Add(persistedResource.CollectionId);
        }
        var davCollections = davCollectionIds.Count == 0
            ? new Dictionary<Guid, DavCollectionOwner>()
            : await database.DavCollections
                .AsNoTracking()
                .Where(collection => davCollectionIds.Contains(collection.Id))
                .Select(collection => new DavCollectionOwner(
                    collection.Id,
                    collection.UserId,
                    collection.CollectionType))
                .ToDictionaryAsync(collection => collection.Id, cancellationToken);
        foreach (var entry in davCollectionEntries.Where(entry => entry.State != EntityState.Deleted))
        {
            davCollections[entry.Entity.Id] = new DavCollectionOwner(
                entry.Entity.Id,
                entry.Entity.UserId,
                entry.Entity.CollectionType);
        }

        var trackedDavResourceIds = davResourceEntries.Select(entry => entry.Entity.Id).ToArray();
        var deletedAddressBookIds = davCollectionEntries
            .Where(entry => entry.State == EntityState.Deleted
                && entry.Entity.CollectionType == DavCollectionDB.AddressBookType)
            .Select(entry => entry.Entity.Id)
            .ToArray();
        var cascadeDeletedDavResources = deletedAddressBookIds.Length == 0
            ? []
            : await database.DavResources
                .AsNoTracking()
                .Where(resource => deletedAddressBookIds.Contains(resource.CollectionId)
                    && !trackedDavResourceIds.Contains(resource.Id))
                .Select(resource => new PersistedDavResource(resource.Id, resource.CollectionId))
                .ToListAsync(cancellationToken);

        var davUserIds = davCollections.Values
            .Where(collection => collection.CollectionType == DavCollectionDB.AddressBookType)
            .Select(collection => collection.UserId)
            .Distinct()
            .ToArray();
        var contactAccounts = await GetPrimaryAccountIdsAsync(
            database,
            davUserIds,
            cancellationToken);

        var changes = new Dictionary<ChangeKey, string>();
        var threadDeltas = new Dictionary<ThreadKey, int>();

        foreach (var entry in davCollectionEntries)
        {
            if (entry.State == EntityState.Modified
                && !IsJmapAddressBookPropertyModified(entry))
            {
                continue;
            }
            var owner = davCollections.GetValueOrDefault(entry.Entity.Id)
                ?? new DavCollectionOwner(
                    entry.Entity.Id,
                    entry.Entity.UserId,
                    entry.Entity.CollectionType);
            if (owner.CollectionType != DavCollectionDB.AddressBookType
                || !contactAccounts.TryGetValue(owner.UserId, out var accountId))
            {
                continue;
            }
            var kind = entry.State switch
            {
                EntityState.Added => Created,
                EntityState.Deleted => Destroyed,
                _ => Updated,
            };
            AddChange(changes, accountId, AddressBookType, $"D{entry.Entity.Id:N}", kind);
        }

        foreach (var entry in davResourceEntries)
        {
            var oldCollectionId = persistedDavResources.TryGetValue(entry.Entity.Id, out var persistedResource)
                ? persistedResource.CollectionId
                : (Guid?)null;
            Guid? newCollectionId = entry.State == EntityState.Deleted
                ? null
                : entry.Entity.CollectionId;
            var oldOwner = oldCollectionId is not null
                ? davCollections.GetValueOrDefault(oldCollectionId.Value)
                : null;
            var newOwner = newCollectionId is not null
                ? davCollections.GetValueOrDefault(newCollectionId.Value)
                : null;
            var oldAccountId = oldOwner is not null
                && oldOwner.CollectionType == DavCollectionDB.AddressBookType
                && contactAccounts.TryGetValue(oldOwner.UserId, out var oldAccount)
                    ? oldAccount
                    : (Guid?)null;
            var newAccountId = newOwner is not null
                && newOwner.CollectionType == DavCollectionDB.AddressBookType
                && contactAccounts.TryGetValue(newOwner.UserId, out var newAccount)
                    ? newAccount
                    : (Guid?)null;
            var objectId = $"C{entry.Entity.Id:N}";
            if (oldAccountId is null && newAccountId is not null)
                AddChange(changes, newAccountId.Value, ContactCardType, objectId, Created);
            else if (oldAccountId is not null && newAccountId is null)
                AddChange(changes, oldAccountId.Value, ContactCardType, objectId, Destroyed);
            else if (oldAccountId is not null && newAccountId is not null)
            {
                if (oldAccountId != newAccountId)
                    AddChange(changes, oldAccountId.Value, ContactCardType, objectId, Destroyed);
                AddChange(
                    changes,
                    newAccountId.Value,
                    ContactCardType,
                    objectId,
                    oldAccountId == newAccountId ? Updated : Created);
            }
        }

        foreach (var resource in cascadeDeletedDavResources)
        {
            if (!davCollections.TryGetValue(resource.CollectionId, out var collection)
                || !contactAccounts.TryGetValue(collection.UserId, out var accountId))
            {
                continue;
            }
            AddChange(changes, accountId, ContactCardType, $"C{resource.Id:N}", Destroyed);
        }
        foreach (var entry in emailEntries)
        {
            var persisted = persistedEmails.GetValueOrDefault(entry.Entity.Id);
            var oldFolderId = persisted?.FolderId;
            var newFolderId = entry.State == EntityState.Deleted
                ? null
                : entry.State == EntityState.Added
                    || entry.Property(email => email.FolderId).IsModified
                        ? entry.Entity.FolderId
                        : persisted?.FolderId;
            var oldAccountId = oldFolderId is not null
                && folderAccounts.TryGetValue(oldFolderId.Value, out var oldAccount)
                    ? oldAccount
                    : (Guid?)null;
            var newAccountId = newFolderId is not null
                && folderAccounts.TryGetValue(newFolderId.Value, out var newAccount)
                    ? newAccount
                    : (Guid?)null;
            var oldVisible = entry.State != EntityState.Added
                && persisted is not null
                && !persisted.IsDeleted;
            var newIsDeleted = (entry.State == EntityState.Added
                || entry.Property(email => email.IsDeleted).IsModified)
                ? entry.Entity.IsDeleted
                : persisted?.IsDeleted ?? entry.Entity.IsDeleted;
            var newVisible = entry.State != EntityState.Deleted && !newIsDeleted;

            var emailObjectId = $"E{entry.Entity.Id:N}";
            if (!oldVisible && newVisible && newAccountId is not null)
                AddChange(changes, newAccountId.Value, EmailType, emailObjectId, Created);
            else if (oldVisible && !newVisible && oldAccountId is not null)
                AddChange(changes, oldAccountId.Value, EmailType, emailObjectId, Destroyed);
            else if (oldVisible && newVisible)
            {
                if (oldAccountId is not null && oldAccountId != newAccountId)
                    AddChange(changes, oldAccountId.Value, EmailType, emailObjectId, Destroyed);
                if (newAccountId is not null)
                {
                    AddChange(
                        changes,
                        newAccountId.Value,
                        EmailType,
                        emailObjectId,
                        oldAccountId == newAccountId ? Updated : Created);
                }
            }
            if (newVisible
                && newAccountId is not null
                && (!oldVisible || oldAccountId != newAccountId))
            {
                AddChange(
                    changes,
                    newAccountId.Value,
                    EmailDeliveryType,
                    emailObjectId,
                    Created);
            }

            if (oldVisible && oldFolderId is not null && oldAccountId is not null)
                AddChange(changes, oldAccountId.Value, MailboxType, $"M{oldFolderId:N}", Updated);
            if (newVisible && newFolderId is not null && newAccountId is not null)
                AddChange(changes, newAccountId.Value, MailboxType, $"M{newFolderId:N}", Updated);

            var oldThread = NormalizeThreadId(persisted?.ThreadObjectId, entry.Entity.Id);
            var newThreadValue = entry.State == EntityState.Deleted
                ? null
                : entry.State == EntityState.Added
                    || entry.Property(email => email.ThreadObjectId).IsModified
                        ? entry.Entity.ThreadObjectId
                        : persisted?.ThreadObjectId;
            if (entry.State == EntityState.Added && string.IsNullOrEmpty(newThreadValue))
            {
                newThreadValue = Guid.CreateVersion7().ToString("N");
                entry.Entity.ThreadObjectId = newThreadValue;
            }
            var newThread = newVisible
                ? NormalizeThreadId(newThreadValue, entry.Entity.Id)
                : null;

            var oldThreadKey = oldAccountId is null || !oldVisible
                ? null
                : new ThreadKey(oldAccountId.Value, oldThread);
            var newThreadKey = newAccountId is null || !newVisible
                ? null
                : new ThreadKey(newAccountId.Value, newThread!);
            if (oldThreadKey != newThreadKey)
            {
                if (oldThreadKey is not null)
                    AddDelta(threadDeltas, oldThreadKey, -1);
                if (newThreadKey is not null)
                    AddDelta(threadDeltas, newThreadKey, 1);
            }
        }

        foreach (var email in cascadeDeletedEmails)
        {
            if (!folderAccounts.TryGetValue(email.FolderId, out var accountId))
                continue;
            AddChange(
                changes,
                accountId,
                EmailType,
                $"E{email.Id:N}",
                Destroyed);
            AddDelta(
                threadDeltas,
                new ThreadKey(accountId, NormalizeThreadId(email.ThreadObjectId, email.Id)),
                -1);
        }

        await AddThreadChangesAsync(database, changes, threadDeltas, cancellationToken);

        foreach (var entry in folderEntries)
        {
            var accountId = entry.Entity.InboxId;
            var kind = entry.State switch
            {
                EntityState.Added => Created,
                EntityState.Deleted => Destroyed,
                _ => Updated,
            };
            AddChange(changes, accountId, MailboxType, $"M{entry.Entity.Id:N}", kind);
        }

        foreach (var entry in inboxEntries)
        {
            if (!IsJmapAccount(entry.Entity))
                continue;
            var kind = entry.State switch
            {
                EntityState.Added => Created,
                EntityState.Deleted => Destroyed,
                _ => Updated,
            };
            AddChange(changes, entry.Entity.Id, IdentityType, $"I{entry.Entity.Id:N}", kind);
        }

        foreach (var entry in submissionEntries)
        {
            var kind = entry.State switch
            {
                EntityState.Added => Created,
                EntityState.Deleted => Destroyed,
                _ => Updated,
            };
            AddChange(
                changes,
                entry.Entity.AccountId,
                SubmissionType,
                $"S{entry.Entity.Id:N}",
                kind);
        }

        if (changedQueueIds.Count > 0)
        {
            var affectedSubmissions = await database.JmapEmailSubmissions
                .AsNoTracking()
                .Where(submission => changedQueueIds.Contains(submission.QueueId))
                .Select(submission => new
                {
                    submission.Id,
                    submission.AccountId,
                })
                .ToListAsync(cancellationToken);
            foreach (var submission in affectedSubmissions)
            {
                AddChange(
                    changes,
                    submission.AccountId,
                    SubmissionType,
                    $"S{submission.Id:N}",
                    Updated);
            }
        }

        foreach (var entry in vacationEntries)
        {
            var kind = entry.State switch
            {
                EntityState.Added => Created,
                EntityState.Deleted => Destroyed,
                _ => Updated,
            };
            AddChange(changes, entry.Entity.AccountId, VacationType, "singleton", kind);
        }

        foreach (var entry in identityEntries)
        {
            var kind = entry.State switch
            {
                EntityState.Added => Created,
                EntityState.Deleted => Destroyed,
                _ => Updated,
            };
            AddChange(
                changes,
                entry.Entity.AccountId,
                IdentityType,
                $"I{entry.Entity.Id:N}",
                kind);
        }

        var now = DateTime.UtcNow;
        foreach (var change in changes)
        {
            database.JmapChanges.Add(new JmapChangeDB
            {
                AccountId = change.Key.AccountId,
                DataType = change.Key.DataType,
                ObjectId = change.Key.ObjectId,
                ChangeKind = change.Value,
                ChangedAt = now,
            });
        }
    }

    private static async Task AddThreadChangesAsync(
        EmailDbContext database,
        IDictionary<ChangeKey, string> changes,
        IReadOnlyDictionary<ThreadKey, int> deltas,
        CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
            return;

        var accountIds = deltas.Keys.Select(key => key.AccountId).Distinct().ToArray();
        var storedThreadIds = deltas.Keys
            .Select(key => key.ThreadId)
            .Where(threadId => threadId.Length > 1 && threadId[0] == 'T')
            .Select(threadId => threadId[1..])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var fallbackEmailIds = storedThreadIds
            .Select(value => Guid.TryParseExact(value, "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        var existing = await database.Emails
            .AsNoTracking()
            .Where(email => accountIds.Contains(email.Folder.InboxId)
                && !email.IsDeleted
                && (email.ThreadObjectId != null
                    && storedThreadIds.Contains(email.ThreadObjectId)
                    || email.ThreadObjectId == null
                    && fallbackEmailIds.Contains(email.Id)))
            .Select(email => new
            {
                AccountId = email.Folder.InboxId,
                email.Id,
                email.ThreadObjectId,
            })
            .ToListAsync(cancellationToken);
        var counts = existing
            .GroupBy(email => new ThreadKey(
                email.AccountId,
                NormalizeThreadId(email.ThreadObjectId, email.Id)))
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var delta in deltas)
        {
            var before = counts.GetValueOrDefault(delta.Key);
            var after = before + delta.Value;
            var kind = before == 0 && after > 0
                ? Created
                : before > 0 && after <= 0
                    ? Destroyed
                    : Updated;
            AddChange(
                changes,
                delta.Key.AccountId,
                ThreadType,
                delta.Key.ThreadId,
                kind);
        }
    }

    private static void AssignDefaultMailboxRoles(
        IEnumerable<EntityEntry<FolderDB>> entries)
    {
        foreach (var entry in entries.Where(entry => entry.State == EntityState.Added
                     && !entry.Entity.SuppressDefaultJmapRole))
        {
            entry.Entity.JmapRole ??= entry.Entity.Name switch
            {
                "Inbox" => "inbox",
                "Sent" => "sent",
                "Drafts" => "drafts",
                "Trash" => "trash",
                "Spam" => "junk",
                _ => null,
            };
        }
    }

    private static bool IsJmapAccount(InboxDB inbox) =>
        inbox.AliasForInboxId is null && inbox.Name != "*";

    private static bool IsJmapAddressBookPropertyModified(
        EntityEntry<DavCollectionDB> entry) =>
        entry.Property(collection => collection.UserId).IsModified
        || entry.Property(collection => collection.CollectionType).IsModified
        || entry.Property(collection => collection.Slug).IsModified
        || entry.Property(collection => collection.DisplayName).IsModified
        || entry.Property(collection => collection.Description).IsModified
        || entry.Property(collection => collection.SortOrder).IsModified
        || entry.Property(collection => collection.IsDefault).IsModified
        || entry.Property(collection => collection.IsSubscribed).IsModified;

    private static async Task<IReadOnlyDictionary<Guid, Guid>> GetPrimaryAccountIdsAsync(
        EmailDbContext database,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, Guid>();
        var ids = userIds.ToArray();
        var candidates = await database.Inboxes
            .AsNoTracking()
            .Where(inbox => ids.Contains(inbox.OwnerId)
                && inbox.AliasForInboxId == null
                && inbox.Name != "*"
                && inbox.Owner.IsActive
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive)
            .Select(inbox => new PrimaryAccountCandidate(
                inbox.Id,
                inbox.OwnerId,
                inbox.Owner.Username,
                inbox.Name,
                inbox.Address.Domain))
            .ToListAsync(cancellationToken);
        return candidates
            .GroupBy(candidate => candidate.UserId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(candidate => string.Equals(
                        candidate.Name + "@" + candidate.Domain,
                        candidate.Username,
                        StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(candidate => candidate.Domain, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                    .Select(candidate => candidate.InboxId)
                    .First());
    }

    private static string NormalizeThreadId(string? storedThreadId, Guid emailId) =>
        "T" + (string.IsNullOrEmpty(storedThreadId) ? emailId.ToString("N") : storedThreadId);

    private static bool IsChanged<TEntity>(EntityEntry<TEntity> entry)
        where TEntity : class =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private static void AddDelta(
        IDictionary<ThreadKey, int> deltas,
        ThreadKey key,
        int delta)
    {
        deltas.TryGetValue(key, out var current);
        deltas[key] = current + delta;
    }

    private static void AddChange(
        IDictionary<ChangeKey, string> changes,
        Guid accountId,
        string dataType,
        string objectId,
        string kind)
    {
        var key = new ChangeKey(accountId, dataType, objectId);
        if (!changes.TryGetValue(key, out var existing))
        {
            changes[key] = kind;
            return;
        }

        var merged = (existing, kind) switch
        {
            (Created, Updated) => Created,
            (Created, Destroyed) => null,
            (Updated, Destroyed) => Destroyed,
            (Destroyed, Created) => Updated,
            (_, Created) => Created,
            (_, Destroyed) => Destroyed,
            _ => existing,
        };
        if (merged is null)
            changes.Remove(key);
        else
            changes[key] = merged;
    }

    private sealed record PersistedEmail(
        Guid Id,
        Guid FolderId,
        string? ThreadObjectId,
        bool IsDeleted);
    private sealed record PersistedDavResource(Guid Id, Guid CollectionId);
    private sealed record DavCollectionOwner(Guid Id, Guid UserId, string CollectionType);
    private sealed record PrimaryAccountCandidate(
        Guid InboxId,
        Guid UserId,
        string Username,
        string Name,
        string Domain);
    private sealed record ChangeKey(Guid AccountId, string DataType, string ObjectId);
    private sealed record ThreadKey(Guid AccountId, string ThreadId);
}
