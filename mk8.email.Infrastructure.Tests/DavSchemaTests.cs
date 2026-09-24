using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class DavSchemaTests
{
    [TestMethod]
    public void DavModelEnforcesTenantAndResourceIdentities()
    {
        using var database = CreateDatabase();
        var collection = database.Model.FindEntityType(typeof(DavCollectionDB));
        var resource = database.Model.FindEntityType(typeof(DavResourceDB));
        var change = database.Model.FindEntityType(typeof(DavChangeDB));
        var share = database.Model.FindEntityType(typeof(DavShareDB));

        Assert.IsNotNull(collection);
        Assert.IsNotNull(resource);
        Assert.IsNotNull(change);
        Assert.IsNotNull(share);
        Assert.AreEqual("dav_collections", collection.GetTableName(), StringComparer.Ordinal);
        Assert.AreEqual("dav_resources", resource.GetTableName(), StringComparer.Ordinal);
        Assert.AreEqual("dav_changes", change.GetTableName(), StringComparer.Ordinal);
        Assert.AreEqual("dav_shares", share.GetTableName(), StringComparer.Ordinal);

        AssertUniqueIndex(collection, nameof(DavCollectionDB.UserId),
            nameof(DavCollectionDB.CollectionType), nameof(DavCollectionDB.Slug));
        var defaultAddressBookIndex = collection.GetIndexes().SingleOrDefault(candidate =>
            candidate.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(DavCollectionDB.UserId)], StringComparer.Ordinal)
            && string.Equals(candidate.GetFilter(), "collection_type = 'addressbook' AND is_default", StringComparison.Ordinal));
        Assert.IsNotNull(defaultAddressBookIndex, "Missing filtered default-address-book index.");
        Assert.IsTrue(defaultAddressBookIndex.IsUnique);
        AssertUniqueIndex(resource, nameof(DavResourceDB.CollectionId),
            nameof(DavResourceDB.ResourceName));
        AssertUniqueIndex(resource, nameof(DavResourceDB.CollectionId),
            nameof(DavResourceDB.Uid));
        var accountUidIndex = resource.GetIndexes().SingleOrDefault(candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(DavResourceDB.AddressBookUserId), nameof(DavResourceDB.Uid)], StringComparer.Ordinal)
            && string.Equals(candidate.GetFilter(), "addressbook_user_id IS NOT NULL", StringComparison.Ordinal));
        Assert.IsNotNull(accountUidIndex, "Missing account-wide ContactCard UID index.");
        Assert.IsTrue(accountUidIndex.IsUnique);
        AssertUniqueIndex(change, nameof(DavChangeDB.CollectionId),
            nameof(DavChangeDB.Sequence));
        AssertUniqueIndex(share, nameof(DavShareDB.CollectionId),
            nameof(DavShareDB.GranteeUserId));

        Assert.AreEqual("text[]", collection.FindProperty(nameof(DavCollectionDB.Components))?.GetColumnType(), StringComparer.Ordinal);
        Assert.AreEqual("is_default", collection.FindProperty(nameof(DavCollectionDB.IsDefault))?.GetColumnName(), StringComparer.Ordinal);
        Assert.AreEqual("is_subscribed", collection.FindProperty(nameof(DavCollectionDB.IsSubscribed))?.GetColumnName(), StringComparer.Ordinal);
        Assert.AreEqual("bytea", resource.FindProperty(nameof(DavResourceDB.Content))?.GetColumnType(), StringComparer.Ordinal);
        Assert.IsTrue(resource.FindProperty(nameof(DavResourceDB.Content))?.IsNullable);
        Assert.AreEqual(32, resource.FindProperty(nameof(DavResourceDB.ObjectProvider))?.GetMaxLength());
        Assert.AreEqual(1024, resource.FindProperty(nameof(DavResourceDB.ObjectName))?.GetMaxLength());
        Assert.AreEqual(64, resource.FindProperty(nameof(DavResourceDB.ObjectSha256))?.GetMaxLength());
        Assert.AreEqual(256, resource.FindProperty(nameof(DavResourceDB.ObjectEntityTag))?.GetMaxLength());
        Assert.IsTrue(collection.GetForeignKeys().All(key => key.DeleteBehavior == DeleteBehavior.Cascade));
        Assert.IsTrue(resource.GetForeignKeys().All(key => key.DeleteBehavior == DeleteBehavior.Cascade));
        Assert.IsTrue(change.GetForeignKeys().All(key => key.DeleteBehavior == DeleteBehavior.Cascade));
        Assert.IsTrue(share.GetForeignKeys().All(key => key.DeleteBehavior == DeleteBehavior.Cascade));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new EmailDbContext(options);
    }

    private static void AssertUniqueIndex(IReadOnlyTypeBase entity, params string[] propertyNames)
    {
        var index = ((IReadOnlyEntityType)entity).GetIndexes().SingleOrDefault(candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames, StringComparer.Ordinal));
        Assert.IsNotNull(index, $"Missing index on {string.Join(", ", propertyNames)}.");
        Assert.IsTrue(index.IsUnique, $"Index on {string.Join(", ", propertyNames)} must be unique.");
    }
}
