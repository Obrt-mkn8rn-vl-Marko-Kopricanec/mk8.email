using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
public sealed class DavAzureBlobPersistenceTests
{
    [TestMethod]
    public async Task ConcurrentLegacyMigrationExternalizesDavBodiesAndRejectsInlineRows()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-dav-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var resourceId = Guid.CreateVersion7();
        var content = Calendar("legacy-dav-resource", "Legacy body");
        await CreateSchemaAsync(databaseServer.ConnectionString);
        var collectionId = await InsertCollectionAsync(databaseServer.ConnectionString);
        await InsertLegacyAsync(
            databaseServer.ConnectionString,
            collectionId,
            resourceId,
            content);

        try
        {
            async Task MigrateAsync()
            {
                await using var context = CreateContext(databaseServer.ConnectionString);
                await new DavResourceLargeObjectMigrationService(
                    context,
                    store,
                    NullLogger<DavResourceLargeObjectMigrationService>.Instance)
                    .MigrateAsync();
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync());

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var migrated = await verification.DavResources.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == resourceId);
            Assert.IsNull(migrated.Content);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.ObjectProvider);
            Assert.AreEqual(
                DavResourceContentService.BuildObjectName(resourceId, migrated.Etag),
                migrated.ObjectName);
            Assert.AreEqual(content.LongLength, migrated.SizeBytes);
            Assert.AreEqual(migrated.Etag, migrated.ObjectSha256);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));

            var effects = new LargeObjectTransactionEffects(
                store,
                NullLogger<LargeObjectTransactionEffects>.Instance);
            var body = await new DavResourceContentService(
                    store,
                    effects,
                    NullLogger<DavResourceContentService>.Instance)
                .ReadAsync(migrated, CancellationToken.None);
            CollectionAssert.AreEqual(content, body);

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertLegacyAsync(
                    databaseServer.ConnectionString,
                    collectionId,
                    Guid.CreateVersion7(),
                    Calendar("forbidden-inline", "Rejected")));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task CallerTransactionsCleanUpCreatedReplacedAndDeletedDavObjects()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-dav-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        await CreateSchemaAsync(databaseServer.ConnectionString);
        var collectionId = await InsertCollectionAsync(databaseServer.ConnectionString);
        await using (var migrationContext = CreateContext(databaseServer.ConnectionString))
        {
            await new DavResourceLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<DavResourceLargeObjectMigrationService>.Instance)
                .MigrateAsync();
        }

        try
        {
            var failedId = Guid.CreateVersion7();
            await using (var failedContext = CreateContext(databaseServer.ConnectionString))
            {
                var effects = Effects(store);
                var marker = effects.Mark();
                await using var transaction = await failedContext.Database.BeginTransactionAsync();
                var failed = NewResource(
                    collectionId,
                    failedId,
                    new string('x', 300),
                    Calendar("failed", "Must roll back"));
                await Content(store, effects).SetAsync(
                    failed,
                    Calendar("failed", "Must roll back"),
                    CancellationToken.None);
                failedContext.DavResources.Add(failed);
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    failedContext.SaveChangesAsync());
                await transaction.RollbackAsync();
                await effects.RollbackAsync(marker);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, failedId));

            var resourceId = Guid.CreateVersion7();
            var firstBody = Calendar("transactional", "First body");
            await using (var createContext = CreateContext(databaseServer.ConnectionString))
            {
                var effects = Effects(store);
                var marker = effects.Mark();
                await using var transaction = await createContext.Database.BeginTransactionAsync();
                var resource = NewResource(
                    collectionId,
                    resourceId,
                    "transactional",
                    firstBody);
                await Content(store, effects).SetAsync(resource, firstBody, CancellationToken.None);
                createContext.DavResources.Add(resource);
                await createContext.SaveChangesAsync();
                await transaction.CommitAsync();
                await effects.CommitAsync(marker);
            }
            var firstNames = await GetBlobNamesAsync(container, resourceId);
            Assert.HasCount(1, firstNames);

            var rollbackBody = Calendar("transactional", "Rolled back body");
            await using (var rollbackContext = CreateContext(databaseServer.ConnectionString))
            {
                var effects = Effects(store);
                var marker = effects.Mark();
                await using var transaction = await rollbackContext.Database.BeginTransactionAsync();
                var resource = await rollbackContext.DavResources.SingleAsync(
                    candidate => candidate.Id == resourceId);
                SetUpdatedIntegrity(resource, rollbackBody);
                await Content(store, effects).SetAsync(
                    resource,
                    rollbackBody,
                    CancellationToken.None);
                await rollbackContext.SaveChangesAsync();
                Assert.HasCount(2, await GetBlobNamesAsync(container, resourceId));
                await transaction.RollbackAsync();
                await effects.RollbackAsync(marker);
            }
            CollectionAssert.AreEqual(firstNames, await GetBlobNamesAsync(container, resourceId));
            await AssertStoredBodyAsync(
                databaseServer.ConnectionString,
                store,
                resourceId,
                firstBody);

            var committedBody = Calendar("transactional", "Committed body");
            await using (var updateContext = CreateContext(databaseServer.ConnectionString))
            {
                var effects = Effects(store);
                var marker = effects.Mark();
                await using var transaction = await updateContext.Database.BeginTransactionAsync();
                var resource = await updateContext.DavResources.SingleAsync(
                    candidate => candidate.Id == resourceId);
                SetUpdatedIntegrity(resource, committedBody);
                await Content(store, effects).SetAsync(
                    resource,
                    committedBody,
                    CancellationToken.None);
                await updateContext.SaveChangesAsync();
                await transaction.CommitAsync();
                await effects.CommitAsync(marker);
            }
            var committedNames = await GetBlobNamesAsync(container, resourceId);
            Assert.HasCount(1, committedNames);
            Assert.AreNotEqual(firstNames[0], committedNames[0]);
            await AssertStoredBodyAsync(
                databaseServer.ConnectionString,
                store,
                resourceId,
                committedBody);

            await using (var deleteContext = CreateContext(databaseServer.ConnectionString))
            {
                var effects = Effects(store);
                var marker = effects.Mark();
                await using var transaction = await deleteContext.Database.BeginTransactionAsync();
                var resource = await deleteContext.DavResources.SingleAsync(
                    candidate => candidate.Id == resourceId);
                Content(store, effects).DeleteOnCommit(resource);
                deleteContext.DavResources.Remove(resource);
                await deleteContext.SaveChangesAsync();
                await transaction.CommitAsync();
                await effects.CommitAsync(marker);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, resourceId));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static DavResourceContentService Content(
        AzureBlobLargeObjectStore store,
        LargeObjectTransactionEffects effects) => new(
        store,
        effects,
        NullLogger<DavResourceContentService>.Instance);

    private static LargeObjectTransactionEffects Effects(AzureBlobLargeObjectStore store) => new(
        store,
        NullLogger<LargeObjectTransactionEffects>.Instance);

    private static AzureBlobLargeObjectStore CreateStore(
        BlobServiceClient serviceClient,
        string containerName) => new(
        serviceClient,
        new AzureBlobLargeObjectStoreOptions
        {
            ContainerName = containerName,
            ObjectPrefix = "objects",
            CreateContainerIfMissing = true,
        });

    private static EmailDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static async Task CreateSchemaAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> InsertCollectionAsync(string connectionString)
    {
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "DAV Azure storage test",
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = $"dav-blob-{Guid.CreateVersion7():N}@example.test",
            PasswordHash = "unused",
            Role = "User",
            CompanyId = company.Id,
            Company = company,
        };
        var collection = new DavCollectionDB
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            User = user,
            CollectionType = DavCollectionDB.CalendarType,
            Slug = "default",
            DisplayName = "Calendar",
            Components = ["VEVENT"],
            SyncToken = 1,
        };
        await using var context = CreateContext(connectionString);
        context.DavCollections.Add(collection);
        await context.SaveChangesAsync();
        return collection.Id;
    }

    private static async Task InsertLegacyAsync(
        string connectionString,
        Guid collectionId,
        Guid resourceId,
        byte[] content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO dav_resources (
                id, collection_id, resource_name, uid, content_type, content,
                etag, size_bytes, change_sequence, created_at, updated_at)
            VALUES (
                @id, @collection_id, @resource_name, @uid, 'text/calendar', @content,
                @etag, @size_bytes, 1, @now, @now)
            """;
        command.Parameters.AddWithValue("id", resourceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("resource_name", $"{resourceId:N}.ics");
        command.Parameters.AddWithValue("uid", $"uid-{resourceId:N}");
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("etag", hash);
        command.Parameters.AddWithValue("size_bytes", content.Length);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private static DavResourceDB NewResource(
        Guid collectionId,
        Guid resourceId,
        string uid,
        byte[] content)
    {
        var resource = new DavResourceDB
        {
            Id = resourceId,
            CollectionId = collectionId,
            ResourceName = $"{resourceId:N}.ics",
            Uid = uid,
            ContentType = "text/calendar",
            ChangeSequence = 1,
        };
        resource.SizeBytes = content.Length;
        SetUpdatedIntegrity(resource, content);
        return resource;
    }

    private static void SetUpdatedIntegrity(DavResourceDB resource, byte[] content)
    {
        resource.Etag = Convert.ToHexStringLower(SHA256.HashData(content));
        resource.UpdatedAt = DateTime.UtcNow;
    }

    private static byte[] Calendar(string uid, string summary) =>
        System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n"
            + $"UID:{uid}\r\nSUMMARY:{summary}\r\n"
            + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static async Task AssertStoredBodyAsync(
        string connectionString,
        AzureBlobLargeObjectStore store,
        Guid resourceId,
        byte[] expected)
    {
        await using var context = CreateContext(connectionString);
        var stored = await context.DavResources.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == resourceId);
        Assert.IsNull(stored.Content);
        var effects = Effects(store);
        var actual = await Content(store, effects).ReadAsync(stored, CancellationToken.None);
        CollectionAssert.AreEqual(expected, actual);
    }

    private static async Task<List<string>> GetBlobNamesAsync(
        BlobContainerClient container,
        Guid resourceId)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           $"objects/dav/resources/{resourceId:N}/",
                           CancellationToken.None))
        {
            names.Add(item.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }

    private static string RequireAzureBlobConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an Azurite or Azure connection string.");
            throw new InvalidOperationException("Azure Blob integration test configuration is required.");
        }
        return connectionString;
    }
}
