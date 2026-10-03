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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DavAzureBlobPersistenceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ConcurrentLegacyMigrationExternalizesDavBodiesAndRejectsInlineRows scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ConcurrentLegacyMigrationExternalizesDavBodiesAndRejectsInlineRows()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-dav-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var resourceId = Guid.CreateVersion7();
        var content = Calendar("legacy-dav-resource", "Legacy body");
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        var collectionId = await InsertCollectionAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        await InsertLegacyAsync(
            databaseServer.ConnectionString,
            collectionId,
            resourceId,
            content).ConfigureAwait(false);

        try
        {
            async Task MigrateAsync()
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                await new DavResourceLargeObjectMigrationService(
                    context,
                    store,
                    NullLogger<DavResourceLargeObjectMigrationService>.Instance)
                    .MigrateAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync()).ConfigureAwait(false);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var migrated = await verification.DavResources.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == resourceId).ConfigureAwait(false);
            Assert.IsNull(migrated.Content);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.ObjectProvider, StringComparer.Ordinal);
            Assert.AreEqual(
                DavResourceContentService.BuildObjectName(resourceId, migrated.Etag),
                migrated.ObjectName, StringComparer.Ordinal);
            Assert.AreEqual(content.LongLength, migrated.SizeBytes);
            Assert.AreEqual(migrated.Etag, migrated.ObjectSha256, StringComparer.Ordinal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));

            var effects = new LargeObjectTransactionEffects(
                store,
                NullLogger<LargeObjectTransactionEffects>.Instance);
            var body = await new DavResourceContentService(
                    store,
                    effects,
                    NullLogger<DavResourceContentService>.Instance)
                .ReadAsync(migrated, CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, body);

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertLegacyAsync(
                    databaseServer.ConnectionString,
                    collectionId,
                    Guid.CreateVersion7(),
                    Calendar("forbidden-inline", "Rejected"))).ConfigureAwait(false);
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState, StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FailedLegacyMigrationRetainsDavRowAndRemovesCreatedBlob()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-dav-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var resourceId = Guid.CreateVersion7();
        var content = Calendar("rollback-dav-resource", "Keep legacy body");
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        var collectionId = await InsertCollectionAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        await InsertLegacyAsync(databaseServer.ConnectionString, collectionId, resourceId, content).ConfigureAwait(false);
        {
            var constraint = CreateContext(databaseServer.ConnectionString);
            await using var constraintLifetime = constraint.ConfigureAwait(false);
            await constraint.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dav_resources ADD CONSTRAINT ck_test_keep_legacy_dav CHECK (content IS NOT NULL)").ConfigureAwait(false);
        }

        try
        {
            {
                var migration = CreateContext(databaseServer.ConnectionString);
                await using var migrationLifetime = migration.ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    new DavResourceLargeObjectMigrationService(
                        migration,
                        store,
                        NullLogger<DavResourceLargeObjectMigrationService>.Instance)
                        .MigrateAsync()).ConfigureAwait(false);
            }

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var legacy = await verification.DavResources.AsNoTracking()
                .SingleAsync(resource => resource.Id == resourceId).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, legacy.Content);
            Assert.IsNull(legacy.ObjectName);
            Assert.HasCount(0, await GetBlobNamesAsync(container, resourceId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The CallerTransactionsCleanUpCreatedReplacedAndDeletedDavObjects scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task CallerTransactionsCleanUpCreatedReplacedAndDeletedDavObjects()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-dav-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        var collectionId = await InsertCollectionAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        {
            var migrationContext = CreateContext(databaseServer.ConnectionString);
            await using var migrationContextLifetime = migrationContext.ConfigureAwait(false);
            await new DavResourceLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<DavResourceLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
        }

        try
        {
            var failedId = Guid.CreateVersion7();
            {
                var failedContext = CreateContext(databaseServer.ConnectionString);
                await using var failedContextLifetime = failedContext.ConfigureAwait(false);
                var effects = Effects(store);
                var marker = effects.Mark();
                var transaction = (await failedContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var failed = NewResource(
                    collectionId,
                    failedId,
                    new string('x', 300),
                    Calendar("failed", "Must roll back"));
                await Content(store, effects).SetAsync(
                    failed,
                    Calendar("failed", "Must roll back"),
                    CancellationToken.None).ConfigureAwait(false);
                await (failedContext.DavResources.AddAsync(failed)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    failedContext.SaveChangesAsync()).ConfigureAwait(false);
                await transaction.RollbackAsync().ConfigureAwait(false);
                await effects.RollbackAsync(marker).ConfigureAwait(false);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, failedId).ConfigureAwait(false));

            var resourceId = Guid.CreateVersion7();
            var firstBody = Calendar("transactional", "First body");
            {
                var createContext = CreateContext(databaseServer.ConnectionString);
                await using var createContextLifetime = createContext.ConfigureAwait(false);
                var effects = Effects(store);
                var marker = effects.Mark();
                var transaction = (await createContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var resource = NewResource(
                    collectionId,
                    resourceId,
                    "transactional",
                    firstBody);
                await Content(store, effects).SetAsync(resource, firstBody, CancellationToken.None).ConfigureAwait(false);
                await (createContext.DavResources.AddAsync(resource)).ConfigureAwait(false);
                await createContext.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                await effects.CommitAsync(marker).ConfigureAwait(false);
            }
            var firstNames = await GetBlobNamesAsync(container, resourceId).ConfigureAwait(false);
            Assert.HasCount(1, firstNames);

            var rollbackBody = Calendar("transactional", "Rolled back body");
            {
                var rollbackContext = CreateContext(databaseServer.ConnectionString);
                await using var rollbackContextLifetime = rollbackContext.ConfigureAwait(false);
                var effects = Effects(store);
                var marker = effects.Mark();
                var transaction = (await rollbackContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var resource = await rollbackContext.DavResources.SingleAsync(
                    candidate => candidate.Id == resourceId).ConfigureAwait(false);
                SetUpdatedIntegrity(resource, rollbackBody);
                await Content(store, effects).SetAsync(
                    resource,
                    rollbackBody,
                    CancellationToken.None).ConfigureAwait(false);
                await rollbackContext.SaveChangesAsync().ConfigureAwait(false);
                Assert.HasCount(2, await GetBlobNamesAsync(container, resourceId).ConfigureAwait(false));
                await transaction.RollbackAsync().ConfigureAwait(false);
                await effects.RollbackAsync(marker).ConfigureAwait(false);
            }
            CollectionAssert.AreEqual(firstNames, await GetBlobNamesAsync(container, resourceId).ConfigureAwait(false));
            await AssertStoredBodyAsync(
                databaseServer.ConnectionString,
                store,
                resourceId,
                firstBody).ConfigureAwait(false);

            var committedBody = Calendar("transactional", "Committed body");
            {
                var updateContext = CreateContext(databaseServer.ConnectionString);
                await using var updateContextLifetime = updateContext.ConfigureAwait(false);
                var effects = Effects(store);
                var marker = effects.Mark();
                var transaction = (await updateContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var resource = await updateContext.DavResources.SingleAsync(
                    candidate => candidate.Id == resourceId).ConfigureAwait(false);
                SetUpdatedIntegrity(resource, committedBody);
                await Content(store, effects).SetAsync(
                    resource,
                    committedBody,
                    CancellationToken.None).ConfigureAwait(false);
                await updateContext.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                await effects.CommitAsync(marker).ConfigureAwait(false);
            }
            var committedNames = await GetBlobNamesAsync(container, resourceId).ConfigureAwait(false);
            Assert.HasCount(1, committedNames);
            Assert.AreNotEqual(firstNames[0], committedNames[0], StringComparer.Ordinal);
            await AssertStoredBodyAsync(
                databaseServer.ConnectionString,
                store,
                resourceId,
                committedBody).ConfigureAwait(false);

            {
                var deleteContext = CreateContext(databaseServer.ConnectionString);
                await using var deleteContextLifetime = deleteContext.ConfigureAwait(false);
                var effects = Effects(store);
                var marker = effects.Mark();
                var transaction = (await deleteContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var resource = await deleteContext.DavResources.SingleAsync(
                    candidate => candidate.Id == resourceId).ConfigureAwait(false);
                Content(store, effects).DeleteOnCommit(resource);
                deleteContext.DavResources.Remove(resource);
                await deleteContext.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                await effects.CommitAsync(marker).ConfigureAwait(false);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, resourceId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
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
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        await (context.DavCollections.AddAsync(collection)).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return collection.Id;
    }

    private static async Task InsertLegacyAsync(
        string connectionString,
        Guid collectionId,
        Guid resourceId,
        byte[] content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
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
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        var stored = await context.DavResources.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == resourceId).ConfigureAwait(false);
        Assert.IsNull(stored.Content);
        var effects = Effects(store);
        var actual = await Content(store, effects).ReadAsync(stored, CancellationToken.None).ConfigureAwait(false);
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
                           CancellationToken.None).ConfigureAwait(false))
        {
            names.Add(item.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
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
