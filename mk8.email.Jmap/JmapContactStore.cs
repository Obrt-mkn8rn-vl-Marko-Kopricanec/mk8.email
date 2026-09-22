using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapContactCardView(
    DavResourceDB Resource,
    JsonObject Card)
{
    public string Id => JmapId.ContactCard(Resource.Id);
    public string AddressBookId => JmapId.AddressBook(Resource.CollectionId);
}

public sealed class JmapContactStore(
    EmailDbContext database,
    EnvironmentConfig environment,
    JmapBlobService blobs,
    DavResourceContentService resourceContent)
{
    public async Task EnsureDefaultAddressBookAsync(
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var existing = await database.DavCollections
            .Where(collection => collection.UserId == user.Id
                && collection.CollectionType == DavCollectionDB.AddressBookType)
            .OrderByDescending(collection => collection.IsDefault)
            .ThenBy(collection => collection.Slug == "default" ? 0 : 1)
            .ThenBy(collection => collection.CreatedAt)
            .ThenBy(collection => collection.Id)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
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
                await database.SaveChangesAsync(cancellationToken);
            return;
        }

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
        database.DavCollections.Add(collection);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.Entry(collection).State = EntityState.Detached;
            if (!await database.DavCollections.AsNoTracking().AnyAsync(candidate =>
                    candidate.UserId == user.Id
                    && candidate.CollectionType == DavCollectionDB.AddressBookType,
                cancellationToken))
            {
                throw;
            }
        }
    }

    internal Task<List<DavCollectionDB>> LoadAddressBooksAsync(
        Guid userId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var query = database.DavCollections
            .Where(collection => collection.UserId == userId
                && collection.CollectionType == DavCollectionDB.AddressBookType)
            .OrderBy(collection => collection.SortOrder)
            .ThenBy(collection => collection.DisplayName)
            .ThenBy(collection => collection.Id);
        return (tracked ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
    }

    internal async Task<List<JmapContactCardView>> LoadCardsAsync(
        Guid userId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var query = database.DavResources
            .Include(resource => resource.Collection)
            .Where(resource => resource.Collection.UserId == userId
                && resource.Collection.CollectionType == DavCollectionDB.AddressBookType)
            .OrderBy(resource => resource.Id);
        var resources = await (tracked ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
        var cards = new List<JmapContactCardView>(resources.Count);
        foreach (var resource in resources)
        {
            var content = await resourceContent.ReadAsync(resource, cancellationToken);
            cards.Add(new JmapContactCardView(
                resource,
                JmapContactCodec.Decode(resource, content)));
        }
        return cards;
    }

    internal async Task<(JsonObject? Card, IReadOnlyList<string> InvalidProperties)> PrepareCardAsync(
        Guid accountId,
        JsonObject requested,
        CancellationToken cancellationToken)
    {
        var card = (JsonObject)requested.DeepClone();
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        if (!JmapContactCodec.TryValidate(card, out var validationErrors))
            invalid.UnionWith(validationErrors);

        IReadOnlyDictionary<string, JsonObject> localizedCards =
            new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (invalid.Count == 0
            && !JmapContactValidator.TryGetLocalizedCards(card, out localizedCards))
        {
            invalid.Add("localizations");
        }

        if (invalid.Count == 0)
            await NormalizeMediaAsync(accountId, card, "media", invalid, cancellationToken);

        if (invalid.Count == 0 && localizedCards.Count > 0)
        {
            foreach (var localized in localizedCards)
            {
                await NormalizeMediaAsync(
                    accountId,
                    localized.Value,
                    $"localizations/{EscapePatchToken(localized.Key)}/media",
                    invalid,
                    cancellationToken);
            }
        }

        if (invalid.Count == 0 && localizedCards.Count > 0)
        {
            var normalizedSource = (JsonObject)card.DeepClone();
            normalizedSource.Remove("localizations");
            var normalizedLocalizations = new JsonObject();
            foreach (var localized in localizedCards)
            {
                normalizedLocalizations[localized.Key] = BuildTopLevelPatch(
                    normalizedSource,
                    localized.Value);
            }
            card["localizations"] = normalizedLocalizations;
            if (!JmapContactCodec.TryValidate(card, out var normalizedErrors))
                invalid.UnionWith(normalizedErrors);
        }

        var encoded = invalid.Count == 0 ? JmapContactCodec.Encode(card) : [];
        if (encoded.LongLength > environment.Dav.MaxResourceSizeBytes)
            invalid.Add("/");
        return invalid.Count == 0
            ? (card, [])
            : (null, invalid.Order(StringComparer.Ordinal).ToArray());
    }

    private async Task NormalizeMediaAsync(
        Guid accountId,
        JsonObject card,
        string path,
        ISet<string> invalid,
        CancellationToken cancellationToken)
    {
        if (card["media"] is not JsonObject media)
            return;
        foreach (var entry in media)
        {
            if (entry.Value is not JsonObject value
                || !value.TryGetPropertyValue("blobId", out var blobNode))
            {
                continue;
            }
            var invalidPath = $"{path}/{EscapePatchToken(entry.Key)}/blobId";
            if (blobNode is not JsonValue blobValue
                || !blobValue.TryGetValue<string>(out var blobId)
                || blobId is null)
            {
                invalid.Add(invalidPath);
                continue;
            }
            var blob = await blobs.GetAsync(accountId, blobId, cancellationToken);
            if (blob is null || !IsRecognizedMedia(value, blob.ContentType))
            {
                invalid.Add(invalidPath);
                continue;
            }
            value.Remove("blobId");
            value["uri"] = $"data:{blob.ContentType};base64,{Convert.ToBase64String(blob.Content)}";
            value["mediaType"] = blob.ContentType;
        }
    }

    private static JsonObject BuildTopLevelPatch(JsonObject source, JsonObject localized)
    {
        var patch = new JsonObject();
        var properties = source.Select(item => item.Key)
            .Concat(localized.Select(item => item.Key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            source.TryGetPropertyValue(property, out var sourceValue);
            var hasLocalized = localized.TryGetPropertyValue(property, out var localizedValue);
            if (hasLocalized && JsonNode.DeepEquals(sourceValue, localizedValue))
                continue;
            patch[EscapePatchToken(property)] = hasLocalized
                ? localizedValue?.DeepClone()
                : null;
        }
        return patch;
    }

    private static string EscapePatchToken(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    internal static JsonObject BuildAddressBook(
        DavCollectionDB collection,
        IReadOnlySet<string>? properties = null)
    {
        var result = new JsonObject { ["id"] = JmapId.AddressBook(collection.Id) };
        if (Wants("name")) result["name"] = collection.DisplayName;
        if (Wants("description")) result["description"] = collection.Description;
        if (Wants("sortOrder")) result["sortOrder"] = collection.SortOrder;
        if (Wants("isDefault")) result["isDefault"] = collection.IsDefault;
        if (Wants("isSubscribed")) result["isSubscribed"] = collection.IsSubscribed;
        if (Wants("shareWith")) result["shareWith"] = null;
        if (Wants("myRights"))
        {
            result["myRights"] = new JsonObject
            {
                ["mayRead"] = true,
                ["mayWrite"] = true,
                ["mayShare"] = false,
                ["mayDelete"] = !collection.IsDefault
                    && !string.Equals(collection.Slug, "default", StringComparison.Ordinal),
            };
        }
        return result;

        bool Wants(string property) => properties is null || properties.Contains(property);
    }

    internal static JsonObject BuildContactCard(
        JmapContactCardView view,
        IReadOnlySet<string>? properties = null)
    {
        var source = view.Card;
        var result = new JsonObject { ["id"] = view.Id };
        if (properties is null)
        {
            foreach (var property in source)
                result[property.Key] = property.Value?.DeepClone();
            result["addressBookIds"] = new JsonObject { [view.AddressBookId] = true };
            return result;
        }

        foreach (var property in properties)
        {
            if (property == "id")
                continue;
            if (property == "addressBookIds")
                result[property] = new JsonObject { [view.AddressBookId] = true };
            else if (source.TryGetPropertyValue(property, out var value))
                result[property] = value?.DeepClone();
        }
        return result;
    }

    internal async Task<DavResourceDB> StoreCreatedCardAsync(
        DavCollectionDB collection,
        Guid id,
        JsonObject card,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var content = JmapContactCodec.Encode(card);
        var resourceName = $"{id:N}.vcf";
        var sequence = checked(++collection.SyncToken);
        var resource = new DavResourceDB
        {
            Id = id,
            CollectionId = collection.Id,
            Collection = collection,
            AddressBookUserId = DavContactUidInvariant.ScopeFor(collection),
            ResourceName = resourceName,
            Uid = card["uid"]!.GetValue<string>(),
            ContentType = "text/vcard",
            Etag = Convert.ToHexStringLower(SHA256.HashData(content)),
            ChangeSequence = sequence,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await resourceContent.SetAsync(resource, content, cancellationToken);
        collection.UpdatedAt = now;
        database.DavResources.Add(resource);
        AddDavChange(database, collection, resourceName, false, resource.Etag, sequence, now);
        return resource;
    }

    internal async Task StoreUpdatedCardAsync(
        DavResourceDB resource,
        DavCollectionDB oldCollection,
        DavCollectionDB newCollection,
        JsonObject card,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var content = JmapContactCodec.Encode(card);
        if (oldCollection.Id != newCollection.Id)
        {
            var oldSequence = checked(++oldCollection.SyncToken);
            oldCollection.UpdatedAt = now;
            AddDavChange(database, oldCollection, resource.ResourceName, true, null, oldSequence, now);
        }
        var sequence = checked(++newCollection.SyncToken);
        newCollection.UpdatedAt = now;
        resource.CollectionId = newCollection.Id;
        resource.Collection = newCollection;
        resource.AddressBookUserId = DavContactUidInvariant.ScopeFor(newCollection);
        resource.Uid = card["uid"]!.GetValue<string>();
        resource.ContentType = "text/vcard";
        resource.Etag = Convert.ToHexStringLower(SHA256.HashData(content));
        resource.ChangeSequence = sequence;
        resource.UpdatedAt = now;
        await resourceContent.SetAsync(resource, content, cancellationToken);
        AddDavChange(database, newCollection, resource.ResourceName, false, resource.Etag, sequence, now);
    }

    internal void DestroyCard(
        DavResourceDB resource,
        DavCollectionDB collection,
        DateTime now)
    {
        var sequence = checked(++collection.SyncToken);
        collection.UpdatedAt = now;
        resourceContent.DeleteOnCommit(resource);
        database.DavResources.Remove(resource);
        AddDavChange(database, collection, resource.ResourceName, true, null, sequence, now);
    }

    private static void AddDavChange(
        EmailDbContext database,
        DavCollectionDB collection,
        string resourceName,
        bool isDeleted,
        string? etag,
        long sequence,
        DateTime now) => database.DavChanges.Add(new DavChangeDB
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Collection = collection,
            Sequence = sequence,
            ResourceName = resourceName,
            IsDeleted = isDeleted,
            Etag = etag,
            ChangedAt = now,
        });

    private static bool IsRecognizedMedia(JsonObject value, string contentType)
    {
        var kind = value["kind"] is JsonValue kindValue
            && kindValue.TryGetValue<string>(out var parsedKind)
                ? parsedKind
                : "photo";
        return kind == "sound"
            ? contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            : contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
