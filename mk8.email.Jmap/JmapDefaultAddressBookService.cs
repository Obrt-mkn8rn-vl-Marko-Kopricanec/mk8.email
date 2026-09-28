using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapDefaultAddressBookService
{
    public static async Task EnsureAsync(EmailDbContext database, AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        var existing = await database.DavCollections
            .Where(collection => collection.UserId == user.Id
                && collection.CollectionType == DavCollectionDB.AddressBookType)
            .OrderByDescending(collection => collection.IsDefault)
            .ThenBy(collection => collection.Slug == "default" ? 0 : 1)
            .ThenBy(collection => collection.CreatedAt)
            .ThenBy(collection => collection.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            await NormalizeExistingAsync(database, existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CreateAsync(database, user, cancellationToken).ConfigureAwait(false);
    }

    private static async Task NormalizeExistingAsync(
        EmailDbContext database, List<DavCollectionDB> existing, CancellationToken cancellationToken)
    {
        var selected = existing[0];
        var changed = false;
        if (!selected.IsDefault)
        {
            selected.IsDefault = true;
            selected.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }
        foreach (var duplicate in existing.Skip(1).Where(collection => collection.IsDefault))
        {
            duplicate.IsDefault = false;
            duplicate.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }
        if (changed)
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateAsync(
        EmailDbContext database, AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var collection = new DavCollectionDB
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            CollectionType = DavCollectionDB.AddressBookType,
            Slug = "default",
            DisplayName = "Address Book",
            IsDefault = true,
            IsSubscribed = true,
            Components = [],
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
            if (!await database.DavCollections.AsNoTracking().AnyAsync(candidate =>
                    candidate.UserId == user.Id
                    && candidate.CollectionType == DavCollectionDB.AddressBookType,
                cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }
}
