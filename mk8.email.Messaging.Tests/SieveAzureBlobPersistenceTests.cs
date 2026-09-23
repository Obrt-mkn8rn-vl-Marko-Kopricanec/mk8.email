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
public sealed class SieveAzureBlobPersistenceTests
{
    [TestMethod]
    public async Task ConcurrentLegacyMigrationExternalizesScriptsAndRejectsInlineRows()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-sieve-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        var scriptId = Guid.CreateVersion7();
        const string original = "require [\"fileinto\"]; keep;";
        var userId = await SeedLegacySchemaAsync(
            databaseServer.ConnectionString,
            scriptId,
            original);

        try
        {
            async Task MigrateAsync()
            {
                await using var context = CreateContext(databaseServer.ConnectionString);
                var effects = CreateEffects(store);
                await new SieveScriptLargeObjectMigrationService(
                    context,
                    store,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptLargeObjectMigrationService>.Instance)
                    .MigrateAsync();
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync());

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var migrated = await verification.SieveScripts.AsNoTracking()
                .SingleAsync(script => script.Id == scriptId);
            Assert.IsNull(migrated.Content);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.ObjectProvider);
            Assert.AreEqual(Encoding.UTF8.GetByteCount(original), migrated.SizeBytes);
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))),
                migrated.ObjectSha256);
            Assert.IsTrue(migrated.ObjectName?.StartsWith(
                $"sieve/scripts/{scriptId:N}/{migrated.ObjectSha256}/",
                StringComparison.Ordinal) == true);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));
            Assert.AreEqual(
                original,
                await new SieveScriptContentService(store, CreateEffects(store))
                    .ReadAsync(migrated, CancellationToken.None));
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId));

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertInlineAsync(
                    databaseServer.ConnectionString,
                    userId,
                    Guid.CreateVersion7(),
                    "forbidden",
                    "keep;"));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task ScriptWriteReplacementAndDeletionKeepOneExternalObject()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-sieve-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        Guid userId;
        await using (var setup = CreateContext(databaseServer.ConnectionString))
        {
            await setup.Database.EnsureCreatedAsync();
            userId = Guid.CreateVersion7();
            setup.Users.Add(new UserDB
            {
                Id = userId,
                Username = $"sieve-{userId:N}@example.test",
                PasswordHash = "unused",
                Role = "User",
            });
            await setup.SaveChangesAsync();
            var effects = CreateEffects(store);
            await new SieveScriptLargeObjectMigrationService(
                setup,
                store,
                new SieveScriptContentService(store, effects),
                effects,
                NullLogger<SieveScriptLargeObjectMigrationService>.Instance)
                .MigrateAsync();
        }

        try
        {
            Guid scriptId;
            await using (var context = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                var service = new SieveScriptService(
                    context,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptService>.Instance);
                Assert.IsTrue((await service.PutAsync(userId, "primary", "keep;")).Succeeded);
                Assert.AreEqual("keep;", (await service.GetAsync(userId, "primary"))?.Content);
                scriptId = await context.SieveScripts
                    .Where(script => script.UserId == userId)
                    .Select(script => script.Id)
                    .SingleAsync();
            }
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId));

            await using (var context = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                var service = new SieveScriptService(
                    context,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptService>.Instance);
                Assert.IsTrue((await service.PutAsync(userId, "primary", "discard;")).Succeeded);
                Assert.AreEqual("discard;", (await service.GetAsync(userId, "primary"))?.Content);
            }
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId));

            await using (var context = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                var service = new SieveScriptService(
                    context,
                    new SieveScriptContentService(store, effects),
                    effects,
                    NullLogger<SieveScriptService>.Instance);
                Assert.IsTrue((await service.DeleteAsync(userId, "primary")).Succeeded);
                Assert.IsNull(await service.GetAsync(userId, "primary"));
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, scriptId));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
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
                    .SetAsync(committed, "keep;", CancellationToken.None));
            Assert.HasCount(2, await GetBlobNamesAsync(container, scriptId));

            await rollbackEffects.RollbackAsync(rollbackMarker);
            await commitEffects.CommitAsync(commitMarker);
            Assert.HasCount(1, await GetBlobNamesAsync(container, scriptId));
            Assert.AreEqual(
                "keep;",
                await new SieveScriptContentService(store, CreateEffects(store))
                    .ReadAsync(committed, CancellationToken.None));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static async Task<Guid> SeedLegacySchemaAsync(
        string connectionString,
        Guid scriptId,
        string scriptContent)
    {
        var userId = Guid.CreateVersion7();
        await using (var context = CreateContext(connectionString))
        {
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new UserDB
            {
                Id = userId,
                Username = $"legacy-sieve-{userId:N}@example.test",
                PasswordHash = "unused",
                Role = "User",
            });
            context.SieveScripts.Add(new SieveScriptDB
            {
                Id = scriptId,
                UserId = userId,
                Name = "legacy",
                Content = scriptContent,
            });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE sieve_scripts
                    DROP COLUMN size_bytes,
                    DROP COLUMN object_provider,
                    DROP COLUMN object_name,
                    DROP COLUMN object_sha256,
                    DROP COLUMN object_etag,
                    ALTER COLUMN content SET NOT NULL;
                """);
        }
        await using (var migrationContext = CreateContext(connectionString))
            await new MailRuntimeSchemaService(migrationContext).EnsureAsync();
        return userId;
    }

    private static async Task InsertInlineAsync(
        string connectionString,
        Guid userId,
        Guid scriptId,
        string name,
        string body)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
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
        await command.ExecuteNonQueryAsync();
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
                           CancellationToken.None))
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
