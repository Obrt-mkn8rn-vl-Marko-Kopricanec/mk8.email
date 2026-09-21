using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Dav;

internal enum DavCollectionKind
{
    Calendar,
    AddressBook,
}

internal sealed record DavCollection(
    Guid Id,
    Guid UserId,
    DavCollectionKind Kind,
    string Slug,
    string DisplayName,
    string? Description,
    string? Color,
    int SortOrder,
    string[] Components,
    long SyncToken,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record DavResource(
    Guid Id,
    Guid CollectionId,
    string ResourceName,
    string Uid,
    string ContentType,
    byte[] Content,
    string Etag,
    int SizeBytes,
    long ChangeSequence,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record DavChange(
    long Sequence,
    string ResourceName,
    bool IsDeleted,
    string? Etag,
    DateTime ChangedAt);

internal sealed record DavCollectionProperties(
    string DisplayName,
    string? Description,
    string? Color,
    int SortOrder,
    string[] Components);

internal enum DavCollectionWriteStatus
{
    Created,
    Updated,
    NotFound,
    AlreadyExists,
    LimitExceeded,
    Protected,
}

internal sealed record DavCollectionWriteResult(
    DavCollectionWriteStatus Status,
    DavCollection? Collection = null);

internal enum DavResourceWriteStatus
{
    Created,
    Updated,
    Unchanged,
    NotFound,
    PreconditionFailed,
    UidConflict,
    LimitExceeded,
}

internal sealed record DavResourceWriteResult(
    DavResourceWriteStatus Status,
    DavResource? Resource = null);

internal sealed class DavStore(EmailDbContext database, EnvironmentConfig environment)
{
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
            cancellationToken);
        await EnsureDefaultCollectionAsync(
            user,
            DavCollectionKind.AddressBook,
            "default",
            "Address Book",
            [],
            cancellationToken);
    }

    public async Task<IReadOnlyList<DavCollection>> GetCollectionsAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultCollectionsAsync(user, cancellationToken);
        var type = ToStoredType(kind);
        var collections = await database.DavCollections
            .AsNoTracking()
            .Where(collection => collection.UserId == user.Id
                && collection.CollectionType == type)
            .OrderBy(collection => collection.SortOrder)
            .ThenBy(collection => collection.DisplayName)
            .ThenBy(collection => collection.Slug)
            .ToListAsync(cancellationToken);
        return collections.Select(ToCollection).ToList();
    }

    public async Task<DavCollection?> GetCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        CancellationToken cancellationToken)
    {
        var type = ToStoredType(kind);
        var collection = await database.DavCollections
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id
                && candidate.CollectionType == type
                && candidate.Slug == slug, cancellationToken);
        return collection is null ? null : ToCollection(collection);
    }

    public async Task<DavCollectionWriteResult> CreateCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        DavCollectionProperties properties,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken);
        if (await database.DavCollections.AnyAsync(collection =>
                collection.UserId == user.Id
                && collection.CollectionType == ToStoredType(kind)
                && collection.Slug == slug,
            cancellationToken))
        {
            return new DavCollectionWriteResult(DavCollectionWriteStatus.AlreadyExists);
        }
        if (await database.DavCollections.CountAsync(
                collection => collection.UserId == user.Id,
                cancellationToken) >= environment.Dav.MaxCollectionsPerUser)
        {
            return new DavCollectionWriteResult(DavCollectionWriteStatus.LimitExceeded);
        }

        var now = DateTime.UtcNow;
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
            Components = NormalizeComponents(kind, properties.Components),
            CreatedAt = now,
            UpdatedAt = now,
        };
        database.DavCollections.Add(collection);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new DavCollectionWriteResult(DavCollectionWriteStatus.AlreadyExists);
        }
        return new DavCollectionWriteResult(
            DavCollectionWriteStatus.Created,
            ToCollection(collection));
    }

    public async Task<DavCollectionWriteResult> UpdateCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        DavCollectionProperties properties,
        CancellationToken cancellationToken)
    {
        var collection = await FindTrackedCollectionAsync(
            user,
            kind,
            slug,
            cancellationToken);
        if (collection is null)
            return new DavCollectionWriteResult(DavCollectionWriteStatus.NotFound);

        collection.DisplayName = properties.DisplayName;
        collection.Description = properties.Description;
        collection.Color = properties.Color;
        collection.SortOrder = properties.SortOrder;
        collection.Components = NormalizeComponents(kind, properties.Components);
        collection.UpdatedAt = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return new DavCollectionWriteResult(
            DavCollectionWriteStatus.Updated,
            ToCollection(collection));
    }

    public async Task<DavCollectionWriteResult> DeleteCollectionAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        CancellationToken cancellationToken)
    {
        if (string.Equals(slug, "default", StringComparison.Ordinal))
            return new DavCollectionWriteResult(DavCollectionWriteStatus.Protected);

        var collection = await FindTrackedCollectionAsync(
            user,
            kind,
            slug,
            cancellationToken);
        if (collection is null)
            return new DavCollectionWriteResult(DavCollectionWriteStatus.NotFound);

        database.DavCollections.Remove(collection);
        await database.SaveChangesAsync(cancellationToken);
        return new DavCollectionWriteResult(DavCollectionWriteStatus.Updated);
    }

    public async Task<IReadOnlyList<DavResource>> GetResourcesAsync(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var resources = await database.DavResources
            .AsNoTracking()
            .Where(resource => resource.CollectionId == collectionId)
            .OrderBy(resource => resource.ResourceName)
            .ToListAsync(cancellationToken);
        return resources.Select(ToResource).ToList();
    }

    public async Task<DavResource?> GetResourceAsync(
        Guid collectionId,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var resource = await database.DavResources
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.CollectionId == collectionId
                && candidate.ResourceName == resourceName, cancellationToken);
        return resource is null ? null : ToResource(resource);
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
            .ToListAsync(cancellationToken);
        return changes.Select(change => new DavChange(
            change.Sequence,
            change.ResourceName,
            change.IsDeleted,
            change.Etag,
            change.ChangedAt)).ToList();
    }

    public async Task<DavResourceWriteResult> PutResourceAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        string resourceName,
        string uid,
        string contentType,
        byte[] content,
        string? ifMatch,
        bool ifNoneMatchStar,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken);
        var collection = await FindTrackedCollectionAsync(
            user,
            kind,
            slug,
            cancellationToken);
        if (collection is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.NotFound);

        var existing = await database.DavResources.SingleOrDefaultAsync(
            resource => resource.CollectionId == collection.Id
                && resource.ResourceName == resourceName,
            cancellationToken);
        if (!PreconditionsAllow(existing?.Etag, existing is not null, ifMatch, ifNoneMatchStar))
            return new DavResourceWriteResult(DavResourceWriteStatus.PreconditionFailed);
        if (existing is null
            && await database.DavResources.CountAsync(
                resource => resource.CollectionId == collection.Id,
                cancellationToken) >= environment.Dav.MaxResourcesPerCollection)
        {
            return new DavResourceWriteResult(DavResourceWriteStatus.LimitExceeded);
        }
        if (await database.DavResources.AnyAsync(resource =>
                resource.CollectionId == collection.Id
                && resource.Uid == uid
                && (existing == null || resource.Id != existing.Id),
            cancellationToken))
        {
            return new DavResourceWriteResult(DavResourceWriteStatus.UidConflict);
        }

        var etag = Convert.ToHexStringLower(SHA256.HashData(content));
        if (existing is not null
            && existing.Etag == etag
            && existing.Uid == uid
            && existing.ContentType == contentType)
        {
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new DavResourceWriteResult(
                DavResourceWriteStatus.Unchanged,
                ToResource(existing));
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
                ResourceName = resourceName,
                CreatedAt = now,
            };
            database.DavResources.Add(resource);
            status = DavResourceWriteStatus.Created;
        }
        else
        {
            resource = existing;
            status = DavResourceWriteStatus.Updated;
        }

        resource.Uid = uid;
        resource.ContentType = contentType;
        resource.Content = content;
        resource.Etag = etag;
        resource.SizeBytes = content.Length;
        resource.ChangeSequence = sequence;
        resource.UpdatedAt = now;
        collection.UpdatedAt = now;
        database.DavChanges.Add(new DavChangeDB
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Sequence = sequence,
            ResourceName = resourceName,
            IsDeleted = false,
            Etag = etag,
            ChangedAt = now,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new DavResourceWriteResult(DavResourceWriteStatus.UidConflict);
        }
        return new DavResourceWriteResult(status, ToResource(resource));
    }

    public async Task<DavResourceWriteResult> DeleteResourceAsync(
        AuthenticatedMailUser user,
        DavCollectionKind kind,
        string slug,
        string resourceName,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginSerializableTransactionAsync(cancellationToken);
        var collection = await FindTrackedCollectionAsync(
            user,
            kind,
            slug,
            cancellationToken);
        if (collection is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.NotFound);
        var resource = await database.DavResources.SingleOrDefaultAsync(
            candidate => candidate.CollectionId == collection.Id
                && candidate.ResourceName == resourceName,
            cancellationToken);
        if (resource is null)
            return new DavResourceWriteResult(DavResourceWriteStatus.NotFound);
        if (!PreconditionsAllow(resource.Etag, exists: true, ifMatch, ifNoneMatchStar: false))
            return new DavResourceWriteResult(DavResourceWriteStatus.PreconditionFailed);

        var now = DateTime.UtcNow;
        var sequence = checked(++collection.SyncToken);
        collection.UpdatedAt = now;
        database.DavResources.Remove(resource);
        database.DavChanges.Add(new DavChangeDB
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Sequence = sequence,
            ResourceName = resourceName,
            IsDeleted = true,
            ChangedAt = now,
        });
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new DavResourceWriteResult(DavResourceWriteStatus.Updated);
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
            cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var collection = new DavCollectionDB
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            CollectionType = type,
            Slug = slug,
            DisplayName = displayName,
            Components = components,
            CreatedAt = now,
            UpdatedAt = now,
        };
        database.DavCollections.Add(collection);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
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
        return await database.DavCollections.SingleOrDefaultAsync(collection =>
            collection.UserId == user.Id
            && collection.CollectionType == type
            && collection.Slug == slug, cancellationToken);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>
        BeginSerializableTransactionAsync(CancellationToken cancellationToken)
    {
        return database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
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
        if (ifMatch.Trim() == "*")
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

    internal static string ToStoredType(DavCollectionKind kind) => kind switch
    {
        DavCollectionKind.Calendar => DavCollectionDB.CalendarType,
        DavCollectionKind.AddressBook => DavCollectionDB.AddressBookType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DavCollection ToCollection(DavCollectionDB collection) => new(
        collection.Id,
        collection.UserId,
        collection.CollectionType == DavCollectionDB.CalendarType
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

    private static DavResource ToResource(DavResourceDB resource) => new(
        resource.Id,
        resource.CollectionId,
        resource.ResourceName,
        resource.Uid,
        resource.ContentType,
        resource.Content,
        resource.Etag,
        resource.SizeBytes,
        resource.ChangeSequence,
        resource.CreatedAt,
        resource.UpdatedAt);
}
