using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
public sealed class JmapAzureBlobPersistenceTests
{
    [TestMethod]
    public async Task ConcurrentLegacyMigrationExternalizesBytesAndEnforcesReferenceOnlyRows()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-jmap-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var accountId = Guid.CreateVersion7();
        var blobId = Guid.CreateVersion7();
        var content = RandomNumberGenerator.GetBytes(16_384);
        await CreateSchemaAsync(databaseServer.ConnectionString);
        await InsertLegacyAsync(
            databaseServer.ConnectionString,
            accountId,
            blobId,
            content);

        try
        {
            async Task MigrateAsync()
            {
                await using var context = CreateContext(databaseServer.ConnectionString);
                var migration = new JmapBlobLargeObjectMigrationService(
                    context,
                    store,
                    NullLogger<JmapBlobLargeObjectMigrationService>.Instance);
                await migration.MigrateAsync();
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync());

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var migrated = await verification.JmapBlobs.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == blobId);
            Assert.IsNull(migrated.Content);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.ObjectProvider);
            Assert.AreEqual($"jmap/uploads/{accountId:N}/{blobId:N}", migrated.ObjectName);
            Assert.AreEqual(content.LongLength, migrated.SizeBytes);
            Assert.AreEqual(64, migrated.ObjectSha256?.Length);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));

            await using var downloaded = new MemoryStream();
            await store.CopyToAsync(ToReference(migrated), downloaded);
            CollectionAssert.AreEqual(content, downloaded.ToArray());

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertLegacyAsync(
                    databaseServer.ConnectionString,
                    accountId,
                    Guid.CreateVersion7(),
                    "forbidden-inline-row"u8.ToArray()));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task CallerTransactionDefersObjectDeletionUntilCommitOrRollback()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-jmap-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var accountId = Guid.CreateVersion7();
        var environment = new EnvironmentConfig
        {
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                MaxUnreferencedBlobBytesPerAccount = 1_000_000,
            },
        };
        await CreateSchemaAsync(databaseServer.ConnectionString);
        await using (var migrationContext = CreateContext(databaseServer.ConnectionString))
        {
            await new JmapBlobLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<JmapBlobLargeObjectMigrationService>.Instance)
                .MigrateAsync();
        }

        try
        {
            JmapBlobDB original;
            await using (var originalContext = CreateContext(databaseServer.ConnectionString))
            {
                var originalEffects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                original = await new JmapBlobService(
                    originalContext,
                    environment,
                    store,
                    originalEffects,
                    NullLogger<JmapBlobService>.Instance)
                    .StoreAsync(
                        accountId,
                        RandomNumberGenerator.GetBytes(600_000),
                        "application/octet-stream",
                        null,
                        CancellationToken.None);
            }

            await using (var rollbackContext = CreateContext(databaseServer.ConnectionString))
            {
                var rollbackEffects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                var marker = rollbackEffects.Mark();
                await using var transaction = await rollbackContext.Database.BeginTransactionAsync();
                await new JmapBlobService(
                    rollbackContext,
                    environment,
                    store,
                    rollbackEffects,
                    NullLogger<JmapBlobService>.Instance)
                    .StoreAsync(
                        accountId,
                        RandomNumberGenerator.GetBytes(600_000),
                        "application/octet-stream",
                        null,
                        CancellationToken.None);
                Assert.HasCount(2, await GetBlobNamesAsync(container, accountId));

                await transaction.RollbackAsync();
                await rollbackEffects.RollbackAsync(marker);
            }

            await using (var rollbackVerification = CreateContext(databaseServer.ConnectionString))
            {
                var rows = await rollbackVerification.JmapBlobs.AsNoTracking().ToListAsync();
                Assert.HasCount(1, rows);
                Assert.AreEqual(original.Id, rows[0].Id);
            }
            var afterRollback = await GetBlobNamesAsync(container, accountId);
            Assert.HasCount(1, afterRollback);
            Assert.AreEqual($"objects/{original.ObjectName}", afterRollback[0]);

            JmapBlobDB committed;
            await using (var commitContext = CreateContext(databaseServer.ConnectionString))
            {
                var commitEffects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                var marker = commitEffects.Mark();
                await using var transaction = await commitContext.Database.BeginTransactionAsync();
                committed = await new JmapBlobService(
                    commitContext,
                    environment,
                    store,
                    commitEffects,
                    NullLogger<JmapBlobService>.Instance)
                    .StoreAsync(
                        accountId,
                        RandomNumberGenerator.GetBytes(600_000),
                        "application/octet-stream",
                        null,
                        CancellationToken.None);
                Assert.HasCount(2, await GetBlobNamesAsync(container, accountId));

                await transaction.CommitAsync();
                await commitEffects.CommitAsync(marker);
            }

            await using (var commitVerification = CreateContext(databaseServer.ConnectionString))
            {
                var rows = await commitVerification.JmapBlobs.AsNoTracking().ToListAsync();
                Assert.HasCount(1, rows);
                Assert.AreEqual(committed.Id, rows[0].Id);
            }
            var afterCommit = await GetBlobNamesAsync(container, accountId);
            Assert.HasCount(1, afterCommit);
            Assert.AreEqual($"objects/{committed.ObjectName}", afterCommit[0]);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentUploadsSerializeAccountQuotaAndReclaimEvictedObjects()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-jmap-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var accountId = Guid.CreateVersion7();
        await CreateSchemaAsync(databaseServer.ConnectionString);
        await using (var migrationContext = CreateContext(databaseServer.ConnectionString))
        {
            await new JmapBlobLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<JmapBlobLargeObjectMigrationService>.Instance)
                .MigrateAsync();
        }

        var environment = new EnvironmentConfig
        {
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                MaxUnreferencedBlobBytesPerAccount = 1_000_000,
            },
        };
        var contents = Enumerable.Range(0, 4)
            .Select(index =>
            {
                var value = RandomNumberGenerator.GetBytes(600_000);
                value[0] = (byte)index;
                return value;
            })
            .ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        try
        {
            async Task StoreAsync(byte[] content)
            {
                await using var context = CreateContext(databaseServer.ConnectionString);
                var effects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                var blobs = new JmapBlobService(
                    context,
                    environment,
                    store,
                    effects,
                    NullLogger<JmapBlobService>.Instance);
                if (Interlocked.Increment(ref ready) == contents.Length)
                    gate.SetResult();
                await gate.Task;
                await blobs.StoreAsync(
                    accountId,
                    content,
                    "application/octet-stream",
                    null,
                    CancellationToken.None);
            }

            await Task.WhenAll(contents.Select(StoreAsync));

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var rows = await verification.JmapBlobs.AsNoTracking().ToListAsync();
            Assert.HasCount(1, rows);
            var surviving = rows[0];
            Assert.IsNull(surviving.Content);
            Assert.AreEqual(600_000L, surviving.SizeBytes);

            var storedNames = await GetBlobNamesAsync(container, accountId);
            Assert.HasCount(1, storedNames);
            Assert.AreEqual($"objects/{surviving.ObjectName}", storedNames[0]);

            await using var downloaded = new MemoryStream();
            await store.CopyToAsync(ToReference(surviving), downloaded);
            var downloadedContent = downloaded.ToArray();
            Assert.IsTrue(contents.Any(candidate =>
                candidate.AsSpan().SequenceEqual(downloadedContent)));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

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

    private static async Task<List<string>> GetBlobNamesAsync(
        BlobContainerClient container,
        Guid accountId)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           $"objects/jmap/uploads/{accountId:N}/",
                           CancellationToken.None))
        {
            names.Add(item.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static LargeObjectReference ToReference(JmapBlobDB blob) => new(
        blob.ObjectProvider
            ?? throw new AssertFailedException("The object provider is missing."),
        blob.ObjectName
            ?? throw new AssertFailedException("The object name is missing."),
        blob.SizeBytes,
        blob.ObjectSha256
            ?? throw new AssertFailedException("The object hash is missing."),
        blob.ObjectEntityTag
            ?? throw new AssertFailedException("The object entity tag is missing."));

    private static async Task CreateSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE jmap_blobs (
                id uuid PRIMARY KEY,
                blob_id varchar(64) NOT NULL UNIQUE,
                account_id uuid NOT NULL,
                content_type varchar(255) NOT NULL,
                name varchar(255),
                content bytea,
                object_provider varchar(32),
                object_name varchar(1024),
                object_sha256 varchar(64),
                object_etag varchar(256),
                size_bytes bigint NOT NULL,
                created_at timestamp with time zone NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_jmap_blobs_size CHECK (size_bytes >= 0),
                CONSTRAINT ck_jmap_blobs_storage_shape CHECK (
                    (content IS NOT NULL
                        AND object_provider IS NULL
                        AND object_name IS NULL
                        AND object_sha256 IS NULL
                        AND object_etag IS NULL)
                    OR
                    (content IS NULL
                        AND object_provider = 'azure-blob'
                        AND object_name IS NOT NULL
                        AND object_sha256 IS NOT NULL
                        AND object_etag IS NOT NULL))
            );
            CREATE INDEX ix_jmap_blobs_account_id_expires_at
                ON jmap_blobs(account_id, expires_at);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLegacyAsync(
        string connectionString,
        Guid accountId,
        Guid blobId,
        byte[] content)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO jmap_blobs (
                id, blob_id, account_id, content_type, content, size_bytes,
                created_at, expires_at)
            VALUES (
                @id, @blob_id, @account_id, 'application/octet-stream', @content,
                @size_bytes, @created_at, @expires_at)
            """;
        command.Parameters.AddWithValue("id", blobId);
        command.Parameters.AddWithValue("blob_id", $"B{blobId:N}");
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("size_bytes", content.LongLength);
        command.Parameters.AddWithValue("created_at", DateTime.UtcNow);
        command.Parameters.AddWithValue("expires_at", DateTime.UtcNow.AddHours(1));
        await command.ExecuteNonQueryAsync();
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
                "Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an Azure Blob-compatible endpoint.");
            throw new InvalidOperationException("Azure Blob-compatible test configuration is required.");
        }
        return connectionString;
    }
}
