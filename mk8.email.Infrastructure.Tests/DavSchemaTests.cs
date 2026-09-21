using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
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
        Assert.AreEqual("dav_collections", collection.GetTableName());
        Assert.AreEqual("dav_resources", resource.GetTableName());
        Assert.AreEqual("dav_changes", change.GetTableName());
        Assert.AreEqual("dav_shares", share.GetTableName());

        AssertUniqueIndex(collection, nameof(DavCollectionDB.UserId),
            nameof(DavCollectionDB.CollectionType), nameof(DavCollectionDB.Slug));
        AssertUniqueIndex(resource, nameof(DavResourceDB.CollectionId),
            nameof(DavResourceDB.ResourceName));
        AssertUniqueIndex(resource, nameof(DavResourceDB.CollectionId),
            nameof(DavResourceDB.Uid));
        AssertUniqueIndex(change, nameof(DavChangeDB.CollectionId),
            nameof(DavChangeDB.Sequence));
        AssertUniqueIndex(share, nameof(DavShareDB.CollectionId),
            nameof(DavShareDB.GranteeUserId));

        Assert.AreEqual("text[]", collection.FindProperty(nameof(DavCollectionDB.Components))?.GetColumnType());
        Assert.AreEqual("bytea", resource.FindProperty(nameof(DavResourceDB.Content))?.GetColumnType());
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
            candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.IsNotNull(index, $"Missing index on {string.Join(", ", propertyNames)}.");
        Assert.IsTrue(index.IsUnique, $"Index on {string.Join(", ", propertyNames)} must be unique.");
    }
}
