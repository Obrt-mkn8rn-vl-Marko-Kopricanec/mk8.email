using System.Security.Cryptography;
using System.Text;
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
internal sealed class SieveAzureBlobPersistenceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ConcurrentLegacyMigrationExternalizesScriptsAndRejectsInlineRows scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ConcurrentLegacyMigrationExternalizesScriptsAndRejectsInlineRows()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-sieve-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        var scriptId = Guid.CreateVersion7();
        const string original = "require [\"fileinto\"]; keep;";
        var userId = await SeedLegacySchemaAsync(
            databaseServer.ConnectionString,
            scriptId,
            original).ConfigureAwait(false);

        try
        {
            async Task MigrateAsync()
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                await new SieveScriptLargeObjectMigrationService(
                    context,
                    store,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptLargeObjectMigrationService>.Instance)
                    .MigrateAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync()).ConfigureAwait(false);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var migrated = await verification.SieveScripts.AsNoTracking()
                .SingleAsync(script => script.Id == scriptId).ConfigureAwait(false);
            Assert.IsNull(migrated.Content);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.ObjectProvider, StringComparer.Ordinal);
            Assert.AreEqual(Encoding.UTF8.GetByteCount(original), migrated.SizeBytes);
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))),
                migrated.ObjectSha256, StringComparer.Ordinal);
            Assert.IsTrue(migrated.ObjectName?.StartsWith(
                $"sieve/scripts/{scriptId:N}/{migrated.ObjectSha256}/",
                StringComparison.Ordinal) == true);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));
            Assert.AreEqual(
                original,
                await new SieveScriptContentService(store, CreateEffects(store))
                    .ReadAsync(migrated, CancellationToken.None).ConfigureAwait(false), StringComparer.Ordinal);
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId).ConfigureAwait(false));

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertInlineAsync(
                    databaseServer.ConnectionString,
                    userId,
                    Guid.CreateVersion7(),
                    "forbidden",
                    "keep;")).ConfigureAwait(false);
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState, StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ScriptWriteReplacementAndDeletionKeepOneExternalObject scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ScriptWriteReplacementAndDeletionKeepOneExternalObject()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-sieve-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        Guid userId;
        {
            var setup = CreateContext(databaseServer.ConnectionString);
            await using var setupLifetime = setup.ConfigureAwait(false);
            await setup.Database.EnsureCreatedAsync().ConfigureAwait(false);
            userId = Guid.CreateVersion7();
            await (setup.Users.AddAsync(new UserDB
            {
                Id = userId,
                Username = $"sieve-{userId:N}@example.test",
                PasswordHash = "unused",
                Role = "User",
            })).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
            var effects = CreateEffects(store);
            await new SieveScriptLargeObjectMigrationService(
                setup,
                store,
                new SieveScriptContentService(store, effects),
                effects,
                NullLogger<SieveScriptLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
        }

        try
        {
            Guid scriptId;
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var service = new SieveScriptService(
                    context,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptService>.Instance);
                Assert.IsTrue((await service.PutAsync(userId, "primary", "keep;").ConfigureAwait(false)).Succeeded);
                Assert.AreEqual("keep;", (await service.GetAsync(userId, "primary").ConfigureAwait(false))?.Content, StringComparer.Ordinal);
                scriptId = await context.SieveScripts
                    .Where(script => script.UserId == userId)
                    .Select(script => script.Id)
                    .SingleAsync().ConfigureAwait(false);
            }
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId).ConfigureAwait(false));

            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var service = new SieveScriptService(
                    context,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptService>.Instance);
                Assert.IsTrue((await service.PutAsync(userId, "primary", "discard;").ConfigureAwait(false)).Succeeded);
                Assert.AreEqual("discard;", (await service.GetAsync(userId, "primary").ConfigureAwait(false))?.Content, StringComparer.Ordinal);
            }
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId).ConfigureAwait(false));

            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var service = new SieveScriptService(
                    context,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptService>.Instance);
                Assert.IsTrue((await service.DeleteAsync(userId, "primary").ConfigureAwait(false)).Succeeded);
                Assert.IsNull(await service.GetAsync(userId, "primary").ConfigureAwait(false));
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, scriptId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentSameContentWritesCannotDeleteEachOthersObject()
    {
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-sieve-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        var scriptId = Guid.CreateVersion7();
        var rolledBack = new SieveScriptDB { Id = scriptId };
        var committed = new SieveScriptDB { Id = scriptId };
        var rollbackEffects = CreateEffects(store);
        var commitEffects = CreateEffects(store);
        var rollbackMarker = rollbackEffects.Mark();
        var commitMarker = commitEffects.Mark();

        try
        {
            await Task.WhenAll(
                new SieveScriptContentService(store, rollbackEffects)
                    .SetAsync(rolledBack, "keep;", CancellationToken.None),
                new SieveScriptContentService(store, commitEffects)
                    .SetAsync(committed, "keep;", CancellationToken.None)).ConfigureAwait(false);
            Assert.HasCount(2, await GetBlobNamesAsync(container, scriptId).ConfigureAwait(false));

            await rollbackEffects.RollbackAsync(rollbackMarker).ConfigureAwait(false);
            await commitEffects.CommitAsync(commitMarker).ConfigureAwait(false);
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId).ConfigureAwait(false));
            Assert.AreEqual(
                "keep;",
                await new SieveScriptContentService(store, CreateEffects(store))
                    .ReadAsync(committed, CancellationToken.None).ConfigureAwait(false), StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Guid> SeedLegacySchemaAsync(
        string connectionString,
        Guid scriptId,
        string scriptContent)
    {
        var userId = Guid.CreateVersion7();
        {
            var context = CreateContext(connectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await (context.Users.AddAsync(new UserDB
            {
                Id = userId,
                Username = $"legacy-sieve-{userId:N}@example.test",
                PasswordHash = "unused",
                Role = "User",
            })).ConfigureAwait(false);
            await (context.SieveScripts.AddAsync(new SieveScriptDB
            {
                Id = scriptId,
                UserId = userId,
                Name = "legacy",
                Content = scriptContent,
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE sieve_scripts
                    DROP COLUMN size_bytes,
                    DROP COLUMN object_provider,
                    DROP COLUMN object_name,
                    DROP COLUMN object_sha256,
                    DROP COLUMN object_etag,
                    ALTER COLUMN content SET NOT NULL;
                """).ConfigureAwait(false);
        }
        {
            var migrationContext = CreateContext(connectionString);
            await using var migrationContextLifetime = migrationContext.ConfigureAwait(false);
            await new MailRuntimeSchemaService(migrationContext).EnsureAsync().ConfigureAwait(false);
        }
        return userId;
    }

    private static async Task InsertInlineAsync(
        string connectionString,
        Guid userId,
        Guid scriptId,
        string name,
        string body)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText =
            """
            INSERT INTO sieve_scripts (
                id, user_id, name, content, is_active, created_at, updated_at)
            VALUES (@id, @user_id, @name, @content, false, @now, @now)
            """;
        command.Parameters.AddWithValue("id", scriptId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("content", body);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<List<string>> GetBlobNamesAsync(
        BlobContainerClient container,
        Guid scriptId)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           $"objects/sieve/scripts/{scriptId:N}/",
                           CancellationToken.None).ConfigureAwait(false))
        {
            names.Add(item.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static AzureBlobLargeObjectStore CreateStore(
        BlobServiceClient client,
        string containerName) => new(
        client,
        new AzureBlobLargeObjectStoreOptions
        {
            ContainerName = containerName,
            ObjectPrefix = "objects",
            CreateContainerIfMissing = true,
        });

    private static LargeObjectTransactionEffects CreateEffects(AzureBlobLargeObjectStore store) =>
        new(store, NullLogger<LargeObjectTransactionEffects>.Instance);

    private static EmailDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(connectionString)
            .Options);

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
