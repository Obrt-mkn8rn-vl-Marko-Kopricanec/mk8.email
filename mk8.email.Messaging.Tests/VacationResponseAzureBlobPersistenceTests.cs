using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
public sealed class VacationResponseAzureBlobPersistenceTests
{
    [TestMethod]
    public async Task LegacyBodiesMigrateAndSurviveDistributedBackupRestore()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The distributed backup integration test requires Linux.");
            return;
        }

        var blobConnection = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blobConnection))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION.");
            return;
        }

        await using var sourceDatabase = await RequirePostgresAsync();
        await using var restoredDatabase = await RequirePostgresAsync();
        var client = new BlobServiceClient(blobConnection);
        var sourceName = $"mk8-vacation-{Guid.NewGuid():N}";
        var restoredName = $"mk8-vacation-restored-{Guid.NewGuid():N}";
        var sourceContainer = client.GetBlobContainerClient(sourceName);
        var restoredContainer = client.GetBlobContainerClient(restoredName);
        var sourceStore = CreateStore(client, sourceName);
        var restoredStore = CreateStore(client, restoredName);
        var accountId = Guid.CreateVersion7();
        var legacyBody = new string('v', 256 * 1024);
        var updatedBody = new string('n', 256 * 1024);
        await SeedLegacyAsync(sourceDatabase.ConnectionString, accountId, legacyBody);
        var parent = Directory.CreateTempSubdirectory("mk8-vacation-backup-");

        try
        {
            await using (var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString))
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var inline = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    DistributedBlobReferenceInventory.ValidateSchemaAsync(
                        connection, transaction));
                StringAssert.Contains(inline.Message, "must be migrated");
            }

            var migrationName = $"mk8-vacation-{accountId:N}";
            var migrationConnection = new NpgsqlConnectionStringBuilder(
                sourceDatabase.ConnectionString)
            {
                ApplicationName = migrationName,
            }.ConnectionString;

            async Task MigrateAsync()
            {
                await using var context = CreateContext(migrationConnection);
                var effects = CreateEffects(sourceStore);
                await new VacationResponseLargeObjectMigrationService(
                    context,
                    sourceStore,
                    new VacationResponseContentService(sourceStore, effects),
                    effects).MigrateAsync();
            }

            await using (var locked = new NpgsqlConnection(sourceDatabase.ConnectionString))
            {
                await locked.OpenAsync();
                await using var lockTransaction = await locked.BeginTransactionAsync();
                await using (var lockRow = locked.CreateCommand())
                {
                    lockRow.Transaction = lockTransaction;
                    lockRow.CommandText = """
                        SELECT account_id FROM jmap_vacation_responses
                        WHERE account_id = @account_id FOR UPDATE
                        """;
                    lockRow.Parameters.AddWithValue("account_id", accountId);
                    Assert.AreEqual(accountId, await lockRow.ExecuteScalarAsync());
                }

                var migrators = Task.WhenAll(MigrateAsync(), MigrateAsync());
                await WaitForBlockedMigrationAsync(
                    sourceDatabase.ConnectionString, migrationName);
                await using (var edit = locked.CreateCommand())
                {
                    edit.Transaction = lockTransaction;
                    edit.CommandText = """
                        UPDATE jmap_vacation_responses
                        SET text_body = @text_body WHERE account_id = @account_id
                        """;
                    edit.Parameters.AddWithValue("text_body", updatedBody);
                    edit.Parameters.AddWithValue("account_id", accountId);
                    Assert.AreEqual(1, await edit.ExecuteNonQueryAsync());
                }
                await lockTransaction.CommitAsync();
                await migrators;
            }

            await using (var context = CreateContext(sourceDatabase.ConnectionString))
            {
                var migrated = await context.JmapVacationResponses.AsNoTracking()
                    .SingleAsync(response => response.AccountId == accountId);
                Assert.IsNull(migrated.TextBody);
                Assert.IsNull(migrated.HtmlBody);
                Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.BodyObjectProvider);
                Assert.IsTrue(migrated.BodySizeBytes > updatedBody.Length);
                Assert.AreEqual((updatedBody, "<p>Away</p>"),
                    await new VacationResponseContentService(sourceStore, CreateEffects(sourceStore))
                        .ReadAsync(migrated, CancellationToken.None));

                var inline = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        "UPDATE jmap_vacation_responses SET text_body = 'x'"));
                Assert.AreEqual(PostgresErrorCodes.CheckViolation, inline.SqlState);
                var incomplete = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        "UPDATE jmap_vacation_responses SET body_object_provider = NULL"));
                Assert.AreEqual(PostgresErrorCodes.CheckViolation, incomplete.SqlState);
            }

            await using var sourceData = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
            await using (var connection = await sourceData.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var rows = new List<DistributedBlobReferenceRow>();
                await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                                   connection, transaction))
                {
                    rows.Add(row);
                }
                Assert.HasCount(1, rows);
                Assert.AreEqual(accountId, rows[0].RowId);
                Assert.AreEqual("jmap_vacation_responses.body_object_name", rows[0].Source);
                Assert.AreEqual(VacationResponseContentService.ContentType, rows[0].ContentType);
            }

            var destination = Path.Combine(parent.FullName, "snapshot");
            var pgDump = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_PG_DUMP")
                ?? "/usr/lib/postgresql/17/bin/pg_dump";
            var exported = await DistributedBackupExporter.ExportAsync(
                sourceData, sourceStore, sourceDatabase.ConnectionString,
                destination, pgDump);
            Assert.AreEqual(1L, exported.ReferenceCount);
            var verified = await DistributedBackupRestorer.VerifyAsync(destination);
            Assert.AreEqual(1L, verified.ReferenceCount);

            await using var targetData = NpgsqlDataSource.Create(restoredDatabase.ConnectionString);
            var pgRestore = Path.Combine(Path.GetDirectoryName(pgDump)!, "pg_restore");
            var restored = await DistributedBackupRestorer.RestoreAsync(
                destination, targetData, restoredDatabase.ConnectionString,
                restoredStore, pgRestore);
            Assert.AreEqual(1L, restored.ReferenceCount);
            await using var targetContext = CreateContext(restoredDatabase.ConnectionString);
            var targetResponse = await targetContext.JmapVacationResponses.AsNoTracking()
                .SingleAsync(response => response.AccountId == accountId);
            Assert.AreEqual((updatedBody, "<p>Away</p>"),
                await new VacationResponseContentService(restoredStore, CreateEffects(restoredStore))
                    .ReadAsync(targetResponse, CancellationToken.None));
            await using var targetConnection = await targetData.OpenConnectionAsync();
            await using var targetTransaction = await targetConnection.BeginTransactionAsync();
            var targetRows = new List<DistributedBlobReferenceRow>();
            await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                               targetConnection, targetTransaction))
            {
                targetRows.Add(row);
            }
            Assert.HasCount(1, targetRows);
            Assert.AreEqual(targetResponse.BodyObjectEntityTag,
                targetRows[0].Reference.EntityTag);
        }
        finally
        {
            await sourceContainer.DeleteIfExistsAsync();
            await restoredContainer.DeleteIfExistsAsync();
            parent.Delete(recursive: true);
        }
    }

    private static async Task WaitForBlockedMigrationAsync(
        string connectionString,
        string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%FOR UPDATE%')
                """;
            check.Parameters.AddWithValue("application_name", applicationName);
            if ((bool)(await check.ExecuteScalarAsync())!)
                return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException(
            "The vacation migration did not wait for the legacy writer's row lock.");
    }

    private static async Task SeedLegacyAsync(
        string connectionString,
        Guid accountId,
        string textBody)
    {
        await using (var context = CreateContext(connectionString))
        {
            await context.Database.EnsureCreatedAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "Vacation migration test",
            };
            var address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "vacation.example.test",
                Company = company,
                IsActive = true,
            };
            var user = new UserDB
            {
                Id = Guid.CreateVersion7(),
                Username = $"vacation-{accountId:N}@example.test",
                PasswordHash = "unused",
                Role = "User",
                Company = company,
            };
            context.Inboxes.Add(new InboxDB
            {
                Id = accountId,
                Name = "vacation",
                Address = address,
                Owner = user,
            });
            await context.SaveChangesAsync();
            context.JmapVacationResponses.Add(new JmapVacationResponseDB
            {
                AccountId = accountId,
                IsEnabled = true,
                TextBody = textBody,
                HtmlBody = "<p>Away</p>",
            });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE jmap_vacation_responses
                    DROP COLUMN body_size_bytes,
                    DROP COLUMN body_object_provider,
                    DROP COLUMN body_object_name,
                    DROP COLUMN body_object_sha256,
                    DROP COLUMN body_object_etag;
                """);
        }

        await using (var context = CreateContext(connectionString))
            await new MailRuntimeSchemaService(context).EnsureAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
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
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
