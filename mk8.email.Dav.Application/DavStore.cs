using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Dav;

internal sealed partial class DavStore(
    EmailDbContext database,
    EnvironmentConfig environment,
    DavResourceContentService resourceContent,
    LargeObjectTransactionEffects transactionEffects,
    ILogger<DavStore> logger)
{
    private const int MaximumSharesPerCollection = 1_000;
    internal const string SchedulingInboxSlug = "schedule-inbox";
    internal const string SchedulingOutboxSlug = "schedule-outbox";

    public async Task EnsureDefaultCollectionsAsync(
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultCollectionAsync(
            user,
            DavCollectionKind.Calendar,
            "default",
            "Calendar",
            ["VEVENT", "VTODO", "VJOURNAL"],
            cancellationToken).ConfigureAwait(false);
        await EnsureDefaultCollectionAsync(
            user,
            DavCollectionKind.AddressBook,
            "default",
            "Address Book",
            [],
            cancellationToken).ConfigureAwait(false);
        await EnsureDefaultCollectionAsync(
            user,
            DavCollectionKind.Calendar,
            SchedulingInboxSlug,
            "Scheduling Inbox",
            ["VEVENT", "VTODO", "VFREEBUSY"],
            cancellationToken).ConfigureAwait(false);
        await EnsureDefaultCollectionAsync(
            user,
            DavCollectionKind.Calendar,
            SchedulingOutboxSlug,
            "Scheduling Outbox",
            ["VEVENT", "VTODO", "VFREEBUSY"],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DavCollection>> GetCollectionsAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultCollectionsAsync(user, cancellationToken).ConfigureAwait(false);
        var companyId = await GetActiveCompanyIdAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (companyId is null)
            return [];
        var type = ToStoredType(kind);
        var collections = await database.DavCollections
            .AsNoTracking()
            .Include(collection => collection.Shares)
            .Where(collection => collection.CollectionType == type
                && collection.Slug != SchedulingInboxSlug
                && collection.Slug != SchedulingOutboxSlug
                && (collection.UserId == user.Id
                    || collection.User.IsActive
                        && collection.User.CompanyId == companyId
                        && collection.User.Company != null
                        && collection.User.Company.IsActive
                        && collection.Shares.Any(share => share.GranteeUserId == user.Id)))
            .OrderBy(collection => collection.SortOrder)
            .ThenBy(collection => collection.DisplayName)
            .ThenBy(collection => collection.Slug)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return collections.Select(collection => ToCollection(collection, user.Id)).ToList();
    }

    public async Task<DavCollection?> GetCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        Guid hrefUserId,
        string hrefSlug,
        CancellationToken cancellationToken)
    {
        if (hrefUserId != user.Id)
            return null;

        var type = ToStoredType(kind);
        DavCollectionDB? collection;
        if (TryParseSharedBindingSlug(hrefSlug, out var sharedCollectionId))
        {
            var companyId = await GetActiveCompanyIdAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (companyId is null)
                return null;
            collection = await database.DavCollections
                .AsNoTracking()
                .Include(candidate => candidate.Shares)
                .SingleOrDefaultAsync(candidate => candidate.Id == sharedCollectionId
                    && candidate.CollectionType == type
                    && candidate.User.IsActive
                    && candidate.User.CompanyId == companyId
                    && candidate.User.Company != null
                    && candidate.User.Company.IsActive
                    && candidate.Shares.Any(share => share.GranteeUserId == user.Id),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            collection = await database.DavCollections
                .AsNoTracking()
                .Include(candidate => candidate.Shares)
                .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id
                    && candidate.CollectionType == type
                    && candidate.Slug == hrefSlug,
                cancellationToken).ConfigureAwait(false);
        }

        return collection is null ? null : ToCollection(collection, user.Id);
    }

    public async Task<DavCollection?> GetOwnedCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        CancellationToken cancellationToken)
    {
        var type = ToStoredType(kind);
        var collection = await database.DavCollections
            .AsNoTracking()
            .Include(candidate => candidate.Shares)
            .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id
                && candidate.CollectionType == type
                && candidate.Slug == slug, cancellationToken).ConfigureAwait(false);
        return collection is null ? null : ToCollection(collection, user.Id);
    }

    // Preserve the ordered collection uniqueness and transaction checks in one write path.
#pragma warning disable MA0051
    public async Task<DavCollectionWriteResult> CreateCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        DavCollectionProperties properties,
        CancellationToken cancellationToken)
    {
#pragma warning restore MA0051
        if (IsSchedulingCollection(slug) || IsSharedBindingSlug(slug))
            return new DavCollectionWriteResult(DavCollectionWriteStatus.Protected);

        // In-memory tests intentionally have no transaction; await using safely skips null.
#pragma warning disable CA2007, MA0004
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007, MA0004
        if (await database.DavCollections.AnyAsync(collection =>
                collection.UserId == user.Id
                && collection.CollectionType == ToStoredType(kind)
                && collection.Slug == slug,
            cancellationToken).ConfigureAwait(false))
        {
            return new DavCollectionWriteResult(DavCollectionWriteStatus.AlreadyExists);
        }
        if (await database.DavCollections.CountAsync(
                collection => collection.UserId == user.Id
                    && collection.Slug != SchedulingInboxSlug
                    && collection.Slug != SchedulingOutboxSlug,
                cancellationToken).ConfigureAwait(false) >= environment.Dav.MaxCollectionsPerUser)
        {
            return new DavCollectionWriteResult(DavCollectionWriteStatus.LimitExceeded);
        }

        var now = DateTime.UtcNow;
        var isDefault = kind == DavCollectionKind.AddressBook
            && string.Equals(slug, "default", StringComparison.Ordinal)
            && !await database.DavCollections.AsNoTracking().AnyAsync(candidate =>
                candidate.UserId == user.Id
                && candidate.CollectionType == DavCollectionDB.AddressBookType
                && candidate.IsDefault,
                cancellationToken).ConfigureAwait(false);
        var collection = new DavCollectionDB
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            CollectionType = ToStoredType(kind),
            Slug = slug,
            DisplayName = properties.DisplayName,
            Description = properties.Description,
            Color = properties.Color,
            SortOrder = properties.SortOrder,
            IsDefault = isDefault,
            IsSubscribed = true,
            Components = NormalizeComponents(kind, properties.Components),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await database.DavCollections.AddAsync(collection, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return new DavCollectionWriteResult(DavCollectionWriteStatus.AlreadyExists);
        }
        return new DavCollectionWriteResult(
            DavCollectionWriteStatus.Created,
            ToCollection(collection, user.Id));
    }

    public async Task<DavCollectionWriteResult> UpdateCollectionAsync(
        AuthenticatedMailUser user,
        DavCollection current,
        DavCollectionProperties properties,
        CancellationToken cancellationToken)
    {
        if (IsSchedulingCollection(current.Slug))
            return new DavCollectionWriteResult(DavCollectionWriteStatus.Protected);
        if (!current.CanWrite)
            return new DavCollectionWriteResult(DavCollectionWriteStatus.Forbidden);

        var collection = await FindTrackedWritableCollectionAsync(
            user,
            current.Id,
            cancellationToken).ConfigureAwait(false);
        if (collection is null)
            return new DavCollectionWriteResult(DavCollectionWriteStatus.NotFound);

        collection.DisplayName = properties.DisplayName;
        collection.Description = properties.Description;
        collection.Color = properties.Color;
        collection.SortOrder = properties.SortOrder;
        collection.Components = NormalizeComponents(current.Kind, properties.Components);
        collection.UpdatedAt = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DavCollectionWriteResult(
            DavCollectionWriteStatus.Updated,
            ToCollection(collection, user.Id));
    }

    public async Task<DavCollectionWriteResult> DeleteCollectionAsync(
        AuthenticatedMailUser user,
        DavCollection current,
        CancellationToken cancellationToken)
    {
        if (!current.IsOwner)
        {
            // Duplicate share rows must be detected, not silently selected.
#pragma warning disable HLQ005
            var share = await database.DavShares.SingleOrDefaultAsync(candidate =>
                candidate.CollectionId == current.Id
                && candidate.GranteeUserId == user.Id,
                cancellationToken).ConfigureAwait(false);
#pragma warning restore HLQ005
            if (share is null)
                return new DavCollectionWriteResult(DavCollectionWriteStatus.NotFound);

            database.DavShares.Remove(share);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new DavCollectionWriteResult(DavCollectionWriteStatus.Updated);
        }

        if (string.Equals(current.Slug, "default", StringComparison.Ordinal)
            || IsSchedulingCollection(current.Slug))
            return new DavCollectionWriteResult(DavCollectionWriteStatus.Protected);

        var collection = await FindTrackedCollectionAsync(
            user,
            current.Kind,
            current.Slug,
            cancellationToken).ConfigureAwait(false);
        if (collection is null)
            return new DavCollectionWriteResult(DavCollectionWriteStatus.NotFound);

        database.DavCollections.Remove(collection);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DavCollectionWriteResult(DavCollectionWriteStatus.Updated);
    }

    public async Task<DavPrincipal?> GetPrincipalAsync(
        AuthenticatedMailUser requester,
        Guid principalId,
        CancellationToken cancellationToken)
    {
        var companyId = await GetActiveCompanyIdAsync(requester.Id, cancellationToken).ConfigureAwait(false);
        if (companyId is null)
            return null;

        return await database.Users
            .AsNoTracking()
            .Where(user => user.Id == principalId
                && user.IsActive
                && user.CompanyId == companyId
                && user.Company != null
                && user.Company.IsActive)
            .Select(user => new DavPrincipal(user.Id, user.Username))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DavPrincipal>> GetPrincipalsAsync(
        AuthenticatedMailUser requester,
        CancellationToken cancellationToken)
    {
        var companyId = await GetActiveCompanyIdAsync(requester.Id, cancellationToken).ConfigureAwait(false);
        if (companyId is null)
            return [];

        return await database.Users
            .AsNoTracking()
            .Where(user => user.IsActive
                && user.CompanyId == companyId
                && user.Company != null
                && user.Company.IsActive)
            .OrderBy(user => user.Username)
            .Take(MaximumSharesPerCollection)
            .Select(user => new DavPrincipal(user.Id, user.Username))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // ACL replacement must validate and apply the complete binding set atomically.
#pragma warning disable MA0051
    public async Task<DavAclWriteResult> ReplaceSharesAsync(
        AuthenticatedMailUser owner,
        Guid collectionId,
        IReadOnlyList<DavShareGrant> grants,
        CancellationToken cancellationToken)
    {
#pragma warning restore MA0051
        if (grants.Count > MaximumSharesPerCollection)
            return new DavAclWriteResult(DavAclWriteStatus.TooManyEntries);
        if (grants.Select(grant => grant.UserId).Distinct().Count() != grants.Count)
            return new DavAclWriteResult(DavAclWriteStatus.BindingConflict);

        // In-memory tests intentionally have no transaction; await using safely skips null.
#pragma warning disable CA2007, MA0004
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007, MA0004
        var collection = await database.DavCollections
            .Include(candidate => candidate.Shares)
            .SingleOrDefaultAsync(candidate => candidate.Id == collectionId,
                cancellationToken).ConfigureAwait(false);
        if (collection is null)
            return new DavAclWriteResult(DavAclWriteStatus.NotFound);
        if (collection.UserId != owner.Id)
            return new DavAclWriteResult(DavAclWriteStatus.Forbidden);
        if (IsSchedulingCollection(collection.Slug))
            return new DavAclWriteResult(DavAclWriteStatus.Protected);
        if (grants.Any(grant => grant.UserId == owner.Id))
            return new DavAclWriteResult(DavAclWriteStatus.Protected);

        var granteeIds = grants.Select(grant => grant.UserId).ToArray();
        if (granteeIds.Length > 0)
        {
            var recognized = await database.Users
                .AsNoTracking()
                .CountAsync(user => granteeIds.Contains(user.Id), cancellationToken).ConfigureAwait(false);
            if (recognized != granteeIds.Length)
                return new DavAclWriteResult(DavAclWriteStatus.UnrecognizedPrincipal);

            var ownerCompanyId = await database.Users
                .AsNoTracking()
                .Where(user => user.Id == owner.Id)
                .Select(user => user.CompanyId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            var allowed = await database.Users
                .AsNoTracking()
                .CountAsync(user => granteeIds.Contains(user.Id)
                    && user.IsActive
                    && user.CompanyId == ownerCompanyId
                    && user.Company != null
                    && user.Company.IsActive,
                cancellationToken).ConfigureAwait(false);
            if (allowed != granteeIds.Length)
                return new DavAclWriteResult(DavAclWriteStatus.DisallowedPrincipal);

            var bindingSlug = SharedBindingSlug(collection.Id);
            if (await database.DavCollections.AsNoTracking().AnyAsync(candidate =>
                    granteeIds.Contains(candidate.UserId)
                    && candidate.CollectionType == collection.CollectionType
                    && candidate.Slug == bindingSlug,
                cancellationToken).ConfigureAwait(false))
            {
                return new DavAclWriteResult(DavAclWriteStatus.BindingConflict);
            }
        }

        var now = DateTime.UtcNow;
        var requested = grants.ToDictionary(grant => grant.UserId);
        // Iterate a snapshot because removing tracked shares may update the navigation collection.
#pragma warning disable HLQ012
        foreach (var existing in collection.Shares.ToList())
#pragma warning restore HLQ012
        {
            if (!requested.Remove(existing.GranteeUserId, out var grant))
            {
                database.DavShares.Remove(existing);
                continue;
            }

            existing.AccessLevel = ToStoredAccess(grant.Access);
            existing.UpdatedAt = now;
        }
        foreach (var grant in requested.Values)
        {
            await database.DavShares.AddAsync(new DavShareDB
            {
                Id = Guid.CreateVersion7(),
                CollectionId = collection.Id,
                GranteeUserId = grant.UserId,
                AccessLevel = ToStoredAccess(grant.Access),
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return new DavAclWriteResult(DavAclWriteStatus.BindingConflict);
        }

        return new DavAclWriteResult(DavAclWriteStatus.Updated);
    }

    public async Task<IReadOnlyList<DavResource>> GetResourcesAsync(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var resources = await database.DavResources
            .AsNoTracking()
            .Where(resource => resource.CollectionId == collectionId)
            .OrderBy(resource => resource.ResourceName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToResourcesAsync(resources, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DavCalendarResourceSet> GetCalendarResourcesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        const int maximumResources = 50_000;
        const long maximumBytes = 256L * 1024 * 1024;
        var query = database.DavResources
            .AsNoTracking()
            .Where(resource => resource.Collection.UserId == userId
                && resource.Collection.CollectionType == DavCollectionDB.CalendarType
                && resource.Collection.Slug != SchedulingInboxSlug
                && resource.Collection.Slug != SchedulingOutboxSlug);
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var totalBytes = await query.SumAsync(
            resource => (long?)resource.SizeBytes,
            cancellationToken).ConfigureAwait(false) ?? 0;
        if (count > maximumResources || totalBytes > maximumBytes)
            return new DavCalendarResourceSet([], false);

        var resources = await query
            .OrderBy(resource => resource.UpdatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new DavCalendarResourceSet(
            await ToResourcesAsync(resources, cancellationToken).ConfigureAwait(false),
            true);
    }

    public async Task<DavCalendarRecipient> ResolveCalendarRecipientAsync(
        string address,
        CancellationToken cancellationToken)
    {
        // Preserve the existing mailbox lookup key used by persisted DAV principals.
#pragma warning disable CA1308
        var normalized = address.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        var separator = normalized.LastIndexOf('@');
        if (separator <= 0 || separator == normalized.Length - 1)
            return new DavCalendarRecipient(normalized, false, null);

        var localPart = normalized[..separator];
        var domain = normalized[(separator + 1)..];
        var route = await database.Inboxes
            .AsNoTracking()
            .Where(inbox => (inbox.Name == localPart || inbox.Name == "*")
                && inbox.Address.Domain == domain
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive
                && (inbox.Name != "*" || inbox.AliasForInboxId != null))
            .OrderBy(inbox => inbox.Name == localPart ? 0 : 1)
            .Select(inbox => new { inbox.Id, inbox.AliasForInboxId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (route is not null)
        {
            var targetId = route.AliasForInboxId ?? route.Id;
            var target = await database.Inboxes
                .AsNoTracking()
                .Where(inbox => inbox.Id == targetId
                    && inbox.Owner.IsActive
                    && inbox.Address.IsActive
                    && inbox.Address.Company.IsActive)
                .Select(inbox => new AuthenticatedMailUser(
                    inbox.OwnerId,
                    inbox.Owner.Username))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return new DavCalendarRecipient(normalized, true, target);
        }

        var principal = await database.Users
            .AsNoTracking()
            .Where(user => user.Username == normalized
                && user.IsActive
                && user.CompanyId != null
                && user.Company != null
                && user.Company.IsActive
                && database.Addresses.Any(hostedDomain =>
                    hostedDomain.CompanyId == user.CompanyId
                    && hostedDomain.Domain == domain
                    && hostedDomain.IsActive))
            .Select(user => new AuthenticatedMailUser(user.Id, user.Username))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var isHostedDomain = principal is not null
            || await database.Addresses.AsNoTracking().AnyAsync(
                addressEntry => addressEntry.Domain == domain
                    && addressEntry.IsActive
                    && addressEntry.Company.IsActive,
                cancellationToken).ConfigureAwait(false);
        return new DavCalendarRecipient(normalized, isHostedDomain, principal);
    }

    public async Task<DavResourceWriteResult> StoreSchedulingMessageAsync(
        AuthenticatedMailUser recipient,
        string resourceName,
        DavContentInfo content,
        byte[] body,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultCollectionsAsync(recipient, cancellationToken).ConfigureAwait(false);
        var collection = await GetOwnedCollectionAsync(
            recipient,
            DavCollectionKind.Calendar,
            SchedulingInboxSlug,
            cancellationToken).ConfigureAwait(false);
        if (collection is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.NotFound);

        return await PutResourceAsync(
            recipient,
            collection.Id,
            resourceName,
            resourceName[..^4],
            content.ContentType,
            body,
            ifMatch: null,
            ifNoneMatchStar: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DavResource?> GetResourceAsync(
        Guid collectionId,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var resource = await database.DavResources
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.CollectionId == collectionId
                && candidate.ResourceName == resourceName, cancellationToken).ConfigureAwait(false);
        return resource is null
            ? null
            : await ToResourceAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DavChange>> GetChangesAsync(
        Guid collectionId,
        long sinceSequence,
        CancellationToken cancellationToken)
    {
        var changes = await database.DavChanges
            .AsNoTracking()
            .Where(change => change.CollectionId == collectionId
                && change.Sequence > sinceSequence)
            .OrderBy(change => change.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return changes.Select(change => new DavChange(
            change.Sequence,
            change.ResourceName,
            change.IsDeleted,
            change.Etag,
            change.ChangedAt)).ToList();
    }

    // Resource preconditions, UID uniqueness, Blob effects, and commit form one ordered write.
#pragma warning disable MA0051
    public async Task<DavResourceWriteResult> PutResourceAsync(
        AuthenticatedMailUser user,
        Guid collectionId,
        string resourceName,
        string uid,
        string contentType,
        byte[] content,
        string? ifMatch,
        bool ifNoneMatchStar,
        CancellationToken cancellationToken)
    {
#pragma warning restore MA0051
        var effectMarker = transactionEffects.Mark();
        // In-memory tests intentionally have no transaction; await using safely skips null.
#pragma warning disable CA2007, MA0004
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007, MA0004
        var commitAttempted = false;
        var collection = await FindTrackedWritableCollectionAsync(
            user,
            collectionId,
            cancellationToken).ConfigureAwait(false);
        if (collection is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.Forbidden);
        if (string.Equals(collection.CollectionType, DavCollectionDB.AddressBookType, StringComparison.Ordinal))
        {
            await DavContactUidInvariant.AcquireAccountLockAsync(
                database,
                collection.UserId,
                cancellationToken).ConfigureAwait(false);
        }

        // Preserve detection of duplicate resource names in a DAV collection.
#pragma warning disable HLQ005
        var existing = await database.DavResources.SingleOrDefaultAsync(
            resource => resource.CollectionId == collection.Id
                && resource.ResourceName == resourceName,
            cancellationToken).ConfigureAwait(false);
#pragma warning restore HLQ005
        if (!PreconditionsAllow(existing?.Etag, existing is not null, ifMatch, ifNoneMatchStar))
            return new DavResourceWriteResult(DavResourceWriteStatus.PreconditionFailed);
        if (existing is null
            && await database.DavResources.CountAsync(
                resource => resource.CollectionId == collection.Id,
                cancellationToken).ConfigureAwait(false) >= environment.Dav.MaxResourcesPerCollection)
        {
            return new DavResourceWriteResult(DavResourceWriteStatus.LimitExceeded);
        }
        if (await database.DavResources.AnyAsync(resource =>
                resource.Uid == uid
                && (collection.CollectionType == DavCollectionDB.AddressBookType
                    ? resource.Collection.UserId == collection.UserId
                        && resource.Collection.CollectionType == DavCollectionDB.AddressBookType
                    : resource.CollectionId == collection.Id)
                && (existing == null || resource.Id != existing.Id),
            cancellationToken).ConfigureAwait(false))
        {
            return new DavResourceWriteResult(DavResourceWriteStatus.UidConflict);
        }

        var etag = Convert.ToHexStringLower(SHA256.HashData(content));
        if (existing is not null
            && string.Equals(existing.Etag, etag, StringComparison.Ordinal)
            && string.Equals(existing.Uid, uid, StringComparison.Ordinal)
            && string.Equals(existing.ContentType, contentType, StringComparison.Ordinal))
        {
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            transactionEffects.Discard(effectMarker);
            return new DavResourceWriteResult(
                DavResourceWriteStatus.Unchanged,
                ToResource(existing, content));
        }

        var now = DateTime.UtcNow;
        var sequence = checked(++collection.SyncToken);
        DavResourceDB resource;
        DavResourceWriteStatus status;
        if (existing is null)
        {
            resource = new DavResourceDB
            {
                Id = Guid.CreateVersion7(),
                CollectionId = collection.Id,
                AddressBookUserId = DavContactUidInvariant.ScopeFor(collection),
                ResourceName = resourceName,
                CreatedAt = now,
            };
            await database.DavResources.AddAsync(resource, cancellationToken).ConfigureAwait(false);
            status = DavResourceWriteStatus.Created;
        }
        else
        {
            resource = existing;
            status = DavResourceWriteStatus.Updated;
        }

        resource.Uid = uid;
        resource.AddressBookUserId = DavContactUidInvariant.ScopeFor(collection);
        resource.ContentType = contentType;
        resource.Etag = etag;
        resource.ChangeSequence = sequence;
        resource.UpdatedAt = now;
        collection.UpdatedAt = now;
        await database.DavChanges.AddAsync(new DavChangeDB
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Sequence = sequence,
            ResourceName = resourceName,
            IsDeleted = false,
            Etag = etag,
            ChangedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            await resourceContent.SetAsync(resource, content, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await transactionEffects.CommitAsync(effectMarker).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            await RollBackAsync(transaction, exception).ConfigureAwait(false);
            await CompleteRollbackAsync(effectMarker, commitAttempted).ConfigureAwait(false);
            return new DavResourceWriteResult(DavResourceWriteStatus.UidConflict);
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction, exception).ConfigureAwait(false);
            await CompleteRollbackAsync(effectMarker, commitAttempted).ConfigureAwait(false);
            throw;
        }
        return new DavResourceWriteResult(status, ToResource(resource, content));
    }

    public async Task<DavResourceWriteResult> DeleteResourceAsync(
        AuthenticatedMailUser user,
        Guid collectionId,
        string resourceName,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var effectMarker = transactionEffects.Mark();
        // In-memory tests intentionally have no transaction; await using safely skips null.
#pragma warning disable CA2007, MA0004
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007, MA0004
        var commitAttempted = false;
        var collection = await FindTrackedWritableCollectionAsync(
            user,
            collectionId,
            cancellationToken).ConfigureAwait(false);
        if (collection is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.Forbidden);
        // Preserve detection of duplicate resource names in a DAV collection.
#pragma warning disable HLQ005
        var resource = await database.DavResources.SingleOrDefaultAsync(
            candidate => candidate.CollectionId == collection.Id
                && candidate.ResourceName == resourceName,
            cancellationToken).ConfigureAwait(false);
#pragma warning restore HLQ005
        if (resource is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.NotFound);
        if (!PreconditionsAllow(resource.Etag, exists: true, ifMatch, ifNoneMatchStar: false))
            return new DavResourceWriteResult(DavResourceWriteStatus.PreconditionFailed);

        var now = DateTime.UtcNow;
        var sequence = checked(++collection.SyncToken);
        collection.UpdatedAt = now;
        resourceContent.DeleteOnCommit(resource);
        database.DavResources.Remove(resource);
        await database.DavChanges.AddAsync(new DavChangeDB
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Sequence = sequence,
            ResourceName = resourceName,
            IsDeleted = true,
            ChangedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await transactionEffects.CommitAsync(effectMarker).ConfigureAwait(false);
            return new DavResourceWriteResult(DavResourceWriteStatus.Updated);
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction, exception).ConfigureAwait(false);
            await CompleteRollbackAsync(effectMarker, commitAttempted).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureDefaultCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        string displayName,
        string[] components,
        CancellationToken cancellationToken)
    {
        var type = ToStoredType(kind);
        if (await database.DavCollections.AsNoTracking().AnyAsync(collection =>
                collection.UserId == user.Id
                && collection.CollectionType == type
                && collection.Slug == slug,
            cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var isDefault = kind == DavCollectionKind.AddressBook
            && string.Equals(slug, "default", StringComparison.Ordinal)
            && !await database.DavCollections.AsNoTracking().AnyAsync(candidate =>
                candidate.UserId == user.Id
                && candidate.CollectionType == DavCollectionDB.AddressBookType
                && candidate.IsDefault,
                cancellationToken).ConfigureAwait(false);
        var collection = new DavCollectionDB
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            CollectionType = type,
            Slug = slug,
            DisplayName = displayName,
            IsDefault = isDefault,
            IsSubscribed = true,
            Components = components,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await database.DavCollections.AddAsync(collection, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            database.Entry(collection).State = EntityState.Detached;
        }
    }

    private async Task<DavCollectionDB?> FindTrackedCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        CancellationToken cancellationToken)
    {
        var type = ToStoredType(kind);
        return await database.DavCollections
            .Include(collection => collection.Shares)
            .SingleOrDefaultAsync(collection => collection.UserId == user.Id
                && collection.CollectionType == type
                && collection.Slug == slug,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DavCollectionDB?> FindTrackedWritableCollectionAsync(
        AuthenticatedMailUser user,
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var companyId = await GetActiveCompanyIdAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (companyId is null)
            return null;
        return await database.DavCollections
            .Include(collection => collection.Shares)
            .SingleOrDefaultAsync(collection => collection.Id == collectionId
                && (collection.UserId == user.Id
                    || collection.User.IsActive
                        && collection.User.CompanyId == companyId
                        && collection.User.Company != null
                        && collection.User.Company.IsActive
                        && collection.Shares.Any(share => share.GranteeUserId == user.Id
                            && share.AccessLevel == DavShareDB.ReadWriteAccess)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid?> GetActiveCompanyIdAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await database.Users
            .AsNoTracking()
            .Where(user => user.Id == userId
                && user.IsActive
                && user.CompanyId != null
                && user.Company != null
                && user.Company.IsActive)
            .Select(user => user.CompanyId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>
        BeginSerializableTransactionAsync(CancellationToken cancellationToken)
    {
        return database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static bool PreconditionsAllow(
        string? currentEtag,
        bool exists,
        string? ifMatch,
        bool ifNoneMatchStar)
    {
        if (ifNoneMatchStar && exists)
            return false;
        if (string.IsNullOrWhiteSpace(ifMatch))
            return true;
        if (!exists)
            return false;
        if (string.Equals(ifMatch.Trim(), "*", StringComparison.Ordinal))
            return true;

        var quoted = QuoteEtag(currentEtag!);
        return ifMatch.Split(',').Any(candidate =>
            string.Equals(candidate.Trim(), quoted, StringComparison.Ordinal));
    }

    internal static string QuoteEtag(string etag) => $"\"{etag}\"";

    private static string[] NormalizeComponents(
        DavCollectionKind kind,
        IEnumerable<string> components)
    {
        if (kind != DavCollectionKind.Calendar)
            return [];

        return components
            .Select(component => component.ToUpperInvariant())
            .Where(component => component is "VEVENT" or "VTODO" or "VJOURNAL")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .DefaultIfEmpty("VEVENT")
            .ToArray();
    }

    internal static bool IsSchedulingCollection(string slug) =>
        string.Equals(slug, SchedulingInboxSlug, StringComparison.Ordinal)
        || string.Equals(slug, SchedulingOutboxSlug, StringComparison.Ordinal);

    internal static bool IsSharedBindingSlug(string slug) =>
        slug.StartsWith("shared-", StringComparison.Ordinal);

    internal static string SharedBindingSlug(Guid collectionId) =>
        $"shared-{collectionId:N}";

    private static bool TryParseSharedBindingSlug(string slug, out Guid collectionId)
    {
        collectionId = default;
        return slug.Length == 39
            && IsSharedBindingSlug(slug)
            && Guid.TryParseExact(slug.AsSpan(7), "N", out collectionId);
    }

    internal static string ToStoredType(DavCollectionKind kind) => kind switch
    {
        DavCollectionKind.Calendar => DavCollectionDB.CalendarType,
        DavCollectionKind.AddressBook => DavCollectionDB.AddressBookType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ToStoredAccess(DavCollectionAccess access) => access switch
    {
        DavCollectionAccess.ReadOnly => DavShareDB.ReadOnlyAccess,
        DavCollectionAccess.ReadWrite => DavShareDB.ReadWriteAccess,
        _ => throw new ArgumentOutOfRangeException(nameof(access)),
    };

    private static DavCollectionAccess FromStoredAccess(string access) => access switch
    {
        DavShareDB.ReadOnlyAccess => DavCollectionAccess.ReadOnly,
        DavShareDB.ReadWriteAccess => DavCollectionAccess.ReadWrite,
        _ => throw new InvalidOperationException("The DAV share has an invalid access level."),
    };

    private static DavCollection ToCollection(
        DavCollectionDB collection,
        Guid requesterId)
    {
        // A shared binding is unique per user; duplicate grants are data corruption.
#pragma warning disable HLQ005
        var access = collection.UserId == requesterId
            ? DavCollectionAccess.Owner
            : collection.Shares
                .Where(share => share.GranteeUserId == requesterId)
                .Select(share => FromStoredAccess(share.AccessLevel))
                .Single();
#pragma warning restore HLQ005
        var hrefSlug = access == DavCollectionAccess.Owner
            ? collection.Slug
            : SharedBindingSlug(collection.Id);
        var shares = collection.Shares
            .OrderBy(share => share.GranteeUserId)
            .Select(share => new DavShareGrant(
                share.GranteeUserId,
                FromStoredAccess(share.AccessLevel)))
            .ToList();

        return new DavCollection(
            collection.Id,
            collection.UserId,
            access,
            requesterId,
            hrefSlug,
            shares,
            string.Equals(collection.CollectionType, DavCollectionDB.CalendarType, StringComparison.Ordinal)
                ? DavCollectionKind.Calendar
                : DavCollectionKind.AddressBook,
            collection.Slug,
            collection.DisplayName,
            collection.Description,
            collection.Color,
            collection.SortOrder,
            collection.Components,
            collection.SyncToken,
            collection.CreatedAt,
            collection.UpdatedAt);
    }

    private async Task<IReadOnlyList<DavResource>> ToResourcesAsync(
        List<DavResourceDB> resources,
        CancellationToken cancellationToken)
    {
        var result = new List<DavResource>(resources.Count);
        // Each resource read awaits Blob-backed content; Span cannot cross those awaits.
#pragma warning disable HLQ012
        foreach (var resource in resources)
#pragma warning restore HLQ012
            result.Add(await ToResourceAsync(resource, cancellationToken).ConfigureAwait(false));
        return result;
    }

    private async Task<DavResource> ToResourceAsync(
        DavResourceDB resource,
        CancellationToken cancellationToken) =>
        ToResource(resource, await resourceContent.ReadAsync(resource, cancellationToken).ConfigureAwait(false));

    private static DavResource ToResource(DavResourceDB resource, byte[] content) => new(
        resource.Id,
        resource.CollectionId,
        resource.ResourceName,
        resource.Uid,
        resource.ContentType,
        content,
        resource.Etag,
        resource.SizeBytes,
        resource.ChangeSequence,
        resource.CreatedAt,
        resource.UpdatedAt);

    private async Task CompleteRollbackAsync(int marker, bool commitAttempted)
    {
        if (commitAttempted)
        {
            transactionEffects.Discard(marker);
            return;
        }
        await transactionEffects.RollbackAsync(marker).ConfigureAwait(false);
    }

    private async Task RollBackAsync(
        IDbContextTransaction? transaction,
        Exception originalException)
    {
        if (transaction is null)
            return;
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        // The original failure remains authoritative when best-effort rollback also fails.
#pragma warning disable CA1031
        catch (Exception rollbackException)
#pragma warning restore CA1031
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                var failureType = originalException.GetType().Name;
                LogRollbackFailure(logger, rollbackException, failureType);
            }
        }
    }

    [LoggerMessage(EventId = 3301, Level = LogLevel.Warning,
        Message = "Could not roll back a DAV resource transaction after {FailureType}")]
    private static partial void LogRollbackFailure(ILogger logger, Exception exception, string failureType);
}
