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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapAzureBlobPersistenceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ConcurrentLegacyMigrationExternalizesBytesAndEnforcesReferenceOnlyRows scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ConcurrentLegacyMigrationExternalizesBytesAndEnforcesReferenceOnlyRows()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-jmap-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var accountId = Guid.CreateVersion7();
        var blobId = Guid.CreateVersion7();
        var content = RandomNumberGenerator.GetBytes(16_384);
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        await InsertLegacyAsync(
            databaseServer.ConnectionString,
            accountId,
            blobId,
            content).ConfigureAwait(false);

        try
        {
            async Task MigrateAsync()
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var migration = new JmapBlobLargeObjectMigrationService(
                    context,
                    store,
                    NullLogger<JmapBlobLargeObjectMigrationService>.Instance);
                await migration.MigrateAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync()).ConfigureAwait(false);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var migrated = await verification.JmapBlobs.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == blobId).ConfigureAwait(false);
            Assert.IsNull(migrated.Content);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.ObjectProvider, StringComparer.Ordinal);
            Assert.AreEqual($"jmap/uploads/{accountId:N}/{blobId:N}", migrated.ObjectName, StringComparer.Ordinal);
            Assert.AreEqual(content.LongLength, migrated.SizeBytes);
            Assert.AreEqual(64, migrated.ObjectSha256?.Length);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));

            var downloaded = new MemoryStream();
            await using var downloadedLifetime = downloaded.ConfigureAwait(false);
            await store.CopyToAsync(ToReference(migrated), downloaded).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, downloaded.ToArray());

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertLegacyAsync(
                    databaseServer.ConnectionString,
                    accountId,
                    Guid.CreateVersion7(),
                    "forbidden-inline-row"u8.ToArray())).ConfigureAwait(false);
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState, StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The CallerTransactionDefersObjectDeletionUntilCommitOrRollback scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task CallerTransactionDefersObjectDeletionUntilCommitOrRollback()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
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
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        {
            var migrationContext = CreateContext(databaseServer.ConnectionString);
            await using var migrationContextLifetime = migrationContext.ConfigureAwait(false);
            await new JmapBlobLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<JmapBlobLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
        }

        try
        {
            JmapBlobDB original;
            {
                var originalContext = CreateContext(databaseServer.ConnectionString);
                await using var originalContextLifetime = originalContext.ConfigureAwait(false);
                var originalEffects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                original = await new JmapBlobService(
                    originalContext,
                    environment,
                    store,
                    originalEffects,
                    new MailboxMessageContentService(store, originalEffects),
                    NullLogger<JmapBlobService>.Instance)
                    .StoreAsync(
                        accountId,
                        RandomNumberGenerator.GetBytes(600_000),
                        "application/octet-stream",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
            }

            {
                var rollbackContext = CreateContext(databaseServer.ConnectionString);
                await using var rollbackContextLifetime = rollbackContext.ConfigureAwait(false);
                var rollbackEffects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                var marker = rollbackEffects.Mark();
                var transaction = (await rollbackContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                await new JmapBlobService(
                    rollbackContext,
                    environment,
                    store,
                    rollbackEffects,
                    new MailboxMessageContentService(store, rollbackEffects),
                    NullLogger<JmapBlobService>.Instance)
                    .StoreAsync(
                        accountId,
                        RandomNumberGenerator.GetBytes(600_000),
                        "application/octet-stream",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(2, await GetBlobNamesAsync(container, accountId).ConfigureAwait(false));

                await transaction.RollbackAsync().ConfigureAwait(false);
                await rollbackEffects.RollbackAsync(marker).ConfigureAwait(false);
            }

            {
                var rollbackVerification = CreateContext(databaseServer.ConnectionString);
                await using var rollbackVerificationLifetime = rollbackVerification.ConfigureAwait(false);
                var rows = await rollbackVerification.JmapBlobs.AsNoTracking().ToListAsync().ConfigureAwait(false);
                Assert.HasCount(1, rows);
                Assert.AreEqual(original.Id, rows[0].Id);
            }
            var afterRollback = await GetBlobNamesAsync(container, accountId).ConfigureAwait(false);
            Assert.HasCount(1, afterRollback);
            Assert.AreEqual($"objects/{original.ObjectName}", afterRollback[0], StringComparer.Ordinal);

            JmapBlobDB committed;
            {
                var commitContext = CreateContext(databaseServer.ConnectionString);
                await using var commitContextLifetime = commitContext.ConfigureAwait(false);
                var commitEffects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                var marker = commitEffects.Mark();
                var transaction = (await commitContext.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                committed = await new JmapBlobService(
                    commitContext,
                    environment,
                    store,
                    commitEffects,
                    new MailboxMessageContentService(store, commitEffects),
                    NullLogger<JmapBlobService>.Instance)
                    .StoreAsync(
                        accountId,
                        RandomNumberGenerator.GetBytes(600_000),
                        "application/octet-stream",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(2, await GetBlobNamesAsync(container, accountId).ConfigureAwait(false));

                await transaction.CommitAsync().ConfigureAwait(false);
                await commitEffects.CommitAsync(marker).ConfigureAwait(false);
            }

            {
                var commitVerification = CreateContext(databaseServer.ConnectionString);
                await using var commitVerificationLifetime = commitVerification.ConfigureAwait(false);
                var rows = await commitVerification.JmapBlobs.AsNoTracking().ToListAsync().ConfigureAwait(false);
                Assert.HasCount(1, rows);
                Assert.AreEqual(committed.Id, rows[0].Id);
            }
            var afterCommit = await GetBlobNamesAsync(container, accountId).ConfigureAwait(false);
            Assert.HasCount(1, afterCommit);
            Assert.AreEqual($"objects/{committed.ObjectName}", afterCommit[0], StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ConcurrentUploadsSerializeAccountQuotaAndReclaimEvictedObjects scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ConcurrentUploadsSerializeAccountQuotaAndReclaimEvictedObjects()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-jmap-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var accountId = Guid.CreateVersion7();
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        {
            var migrationContext = CreateContext(databaseServer.ConnectionString);
            await using var migrationContextLifetime = migrationContext.ConfigureAwait(false);
            await new JmapBlobLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<JmapBlobLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
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
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = new LargeObjectTransactionEffects(
                    store,
                    NullLogger<LargeObjectTransactionEffects>.Instance);
                var blobs = new JmapBlobService(
                    context,
                    environment,
                    store,
                    effects,
                    new MailboxMessageContentService(store, effects),
                    NullLogger<JmapBlobService>.Instance);
                if (Interlocked.Increment(ref ready) == contents.Length)
                    gate.SetResult();

                // This async test intentionally joins its pre-started background operation; no foreground synchronization context or JTF is involved.
#pragma warning disable VSTHRD003
                await gate.Task.ConfigureAwait(false);

#pragma warning restore VSTHRD003

                await blobs.StoreAsync(
                    accountId,
                    content,
                    "application/octet-stream",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            await Task.WhenAll(contents.Select(StoreAsync)).ConfigureAwait(false);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var rows = await verification.JmapBlobs.AsNoTracking().ToListAsync().ConfigureAwait(false);
            Assert.HasCount(1, rows);
            var surviving = rows[0];
            Assert.IsNull(surviving.Content);
            Assert.AreEqual(600_000L, surviving.SizeBytes);

            var storedNames = await GetBlobNamesAsync(container, accountId).ConfigureAwait(false);
            Assert.HasCount(1, storedNames);
            Assert.AreEqual($"objects/{surviving.ObjectName}", storedNames[0], StringComparer.Ordinal);

            var downloaded = new MemoryStream();
            await using var downloadedLifetime = downloaded.ConfigureAwait(false);
            await store.CopyToAsync(ToReference(surviving), downloaded).ConfigureAwait(false);
            var downloadedContent = downloaded.ToArray();
            Assert.IsTrue(contents.Any(candidate =>
                candidate.AsSpan().SequenceEqual(downloadedContent)));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
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
                           CancellationToken.None).ConfigureAwait(false))
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
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
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
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task InsertLegacyAsync(
        string connectionString,
        Guid accountId,
        Guid blobId,
        byte[] content)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
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
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                "Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an Azure Blob-compatible endpoint.");
            throw new InvalidOperationException("Azure Blob-compatible test configuration is required.");
        }
        return connectionString;
    }
}
