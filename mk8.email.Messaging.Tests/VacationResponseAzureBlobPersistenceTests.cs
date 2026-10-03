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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class VacationResponseAzureBlobPersistenceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The LegacyBodiesMigrateAndSurviveDistributedBackupRestore scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
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

        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var restoredDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var restoredDatabaseLifetime = restoredDatabase.ConfigureAwait(false);
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
        await SeedLegacyAsync(sourceDatabase.ConnectionString, accountId, legacyBody).ConfigureAwait(false);
        var parent = Directory.CreateTempSubdirectory("mk8-vacation-backup-");

        try
        {
            {
                var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
                await using var sourceLifetime = source.ConfigureAwait(false);
                var connection = (await source.OpenConnectionAsync().ConfigureAwait(false));
                await using var connectionLifetime = connection.ConfigureAwait(false);
                var transaction = (await connection.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var inline = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    DistributedBlobReferenceInventory.ValidateSchemaAsync(
                        connection, transaction)).ConfigureAwait(false);
                StringAssert.Contains(inline.Message, "must be migrated", StringComparison.Ordinal);
            }

            var migrationName = $"mk8-vacation-{accountId:N}";
            var migrationConnection = new NpgsqlConnectionStringBuilder(
                sourceDatabase.ConnectionString)
            {
                ApplicationName = migrationName,
            }.ConnectionString;

            async Task MigrateAsync()
            {
                var context = CreateContext(migrationConnection);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(sourceStore);
                await new VacationResponseLargeObjectMigrationService(
                    context,
                    sourceStore,
                    new VacationResponseContentService(sourceStore, effects),
                    effects).MigrateAsync().ConfigureAwait(false);
            }

            {
                var locked = new NpgsqlConnection(sourceDatabase.ConnectionString);
                await using var lockedLifetime = locked.ConfigureAwait(false);
                await locked.OpenAsync().ConfigureAwait(false);
                var lockTransaction = (await locked.BeginTransactionAsync().ConfigureAwait(false));
                await using var lockTransactionLifetime = lockTransaction.ConfigureAwait(false);
                {
                    var lockRow = locked.CreateCommand();
                    await using var lockRowLifetime = lockRow.ConfigureAwait(false);
                    lockRow.Transaction = lockTransaction;
                    lockRow.CommandText = """
                        SELECT account_id FROM jmap_vacation_responses
                        WHERE account_id = @account_id FOR UPDATE
                        """;
                    lockRow.Parameters.AddWithValue("account_id", accountId);
                    Assert.AreEqual(accountId, await lockRow.ExecuteScalarAsync().ConfigureAwait(false));
                }

                var migrators = Task.WhenAll(MigrateAsync(), MigrateAsync());
                await WaitForBlockedMigrationAsync(
                    sourceDatabase.ConnectionString, migrationName).ConfigureAwait(false);
                {
                    var edit = locked.CreateCommand();
                    await using var editLifetime = edit.ConfigureAwait(false);
                    edit.Transaction = lockTransaction;
                    edit.CommandText = """
                        UPDATE jmap_vacation_responses
                        SET text_body = @text_body WHERE account_id = @account_id
                        """;
                    edit.Parameters.AddWithValue("text_body", updatedBody);
                    edit.Parameters.AddWithValue("account_id", accountId);
                    Assert.AreEqual(1, await edit.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
                await lockTransaction.CommitAsync().ConfigureAwait(false);
                await migrators.ConfigureAwait(false);
            }

            {
                var context = CreateContext(sourceDatabase.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var migrated = await context.JmapVacationResponses.AsNoTracking()
                    .SingleAsync(response => response.AccountId == accountId).ConfigureAwait(false);
                Assert.IsNull(migrated.TextBody);
                Assert.IsNull(migrated.HtmlBody);
                Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.BodyObjectProvider, StringComparer.Ordinal);
                Assert.IsTrue(migrated.BodySizeBytes > updatedBody.Length);
                Assert.AreEqual((updatedBody, "<p>Away</p>"),
                    await new VacationResponseContentService(sourceStore, CreateEffects(sourceStore))
                        .ReadAsync(migrated, CancellationToken.None).ConfigureAwait(false));

                var inline = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        "UPDATE jmap_vacation_responses SET text_body = 'x'")).ConfigureAwait(false);
                Assert.AreEqual(PostgresErrorCodes.CheckViolation, inline.SqlState, StringComparer.Ordinal);
                var incomplete = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        "UPDATE jmap_vacation_responses SET body_object_provider = NULL")).ConfigureAwait(false);
                Assert.AreEqual(PostgresErrorCodes.CheckViolation, incomplete.SqlState, StringComparer.Ordinal);
            }

            var sourceData = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
            await using var sourceDataLifetime = sourceData.ConfigureAwait(false);
            {
                var connection = (await sourceData.OpenConnectionAsync().ConfigureAwait(false));
                await using var connectionLifetime = connection.ConfigureAwait(false);
                var transaction = (await connection.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var rows = new List<DistributedBlobReferenceRow>();
                await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                                   connection, transaction).ConfigureAwait(false))
                {
                    rows.Add(row);
                }
                Assert.HasCount(1, rows);
                Assert.AreEqual(accountId, rows[0].RowId);
                Assert.AreEqual("jmap_vacation_responses.body_object_name", rows[0].Source, StringComparer.Ordinal);
                Assert.AreEqual(VacationResponseContentService.ContentType, rows[0].ContentType, StringComparer.Ordinal);
            }

            var destination = Path.Combine(parent.FullName, "snapshot");
            var pgDump = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_PG_DUMP")
                ?? "/usr/lib/postgresql/17/bin/pg_dump";
            var exported = await DistributedBackupExporter.ExportAsync(
                sourceData, sourceStore, sourceDatabase.ConnectionString,
                destination, pgDump).ConfigureAwait(false);
            Assert.AreEqual(1L, exported.ReferenceCount);
            var verified = await DistributedBackupRestorer.VerifyAsync(destination).ConfigureAwait(false);
            Assert.AreEqual(1L, verified.ReferenceCount);

            var targetData = NpgsqlDataSource.Create(restoredDatabase.ConnectionString);
            await using var targetDataLifetime = targetData.ConfigureAwait(false);
            var pgRestore = Path.Combine(Path.GetDirectoryName(pgDump)!, "pg_restore");
            var restored = await DistributedBackupRestorer.RestoreAsync(
                destination, targetData, restoredDatabase.ConnectionString,
                restoredStore, pgRestore).ConfigureAwait(false);
            Assert.AreEqual(1L, restored.ReferenceCount);
            var targetContext = CreateContext(restoredDatabase.ConnectionString);
            await using var targetContextLifetime = targetContext.ConfigureAwait(false);
            var targetResponse = await targetContext.JmapVacationResponses.AsNoTracking()
                .SingleAsync(response => response.AccountId == accountId).ConfigureAwait(false);
            Assert.AreEqual((updatedBody, "<p>Away</p>"),
                await new VacationResponseContentService(restoredStore, CreateEffects(restoredStore))
                    .ReadAsync(targetResponse, CancellationToken.None).ConfigureAwait(false));
            var targetConnection = (await targetData.OpenConnectionAsync().ConfigureAwait(false));
            await using var targetConnectionLifetime = targetConnection.ConfigureAwait(false);
            var targetTransaction = (await targetConnection.BeginTransactionAsync().ConfigureAwait(false));
            await using var targetTransactionLifetime = targetTransaction.ConfigureAwait(false);
            var targetRows = new List<DistributedBlobReferenceRow>();
            await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                               targetConnection, targetTransaction).ConfigureAwait(false))
            {
                targetRows.Add(row);
            }
            Assert.HasCount(1, targetRows);
            Assert.AreEqual(targetResponse.BodyObjectEntityTag,
                targetRows[0].Reference.EntityTag, StringComparer.Ordinal);
        }
        finally
        {
            await sourceContainer.DeleteIfExistsAsync().ConfigureAwait(false);
            await restoredContainer.DeleteIfExistsAsync().ConfigureAwait(false);
            parent.Delete(recursive: true);
        }
    }

    private static async Task WaitForBlockedMigrationAsync(
        string connectionString,
        string applicationName)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var check = connection.CreateCommand();
            await using var checkLifetime = check.ConfigureAwait(false);
            check.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%FOR UPDATE%')
                """;
            check.Parameters.AddWithValue("application_name", applicationName);
            if ((bool)(await check.ExecuteScalarAsync().ConfigureAwait(false))!)
                return;
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            "The vacation migration did not wait for the legacy writer's row lock.");
    }

    private static async Task SeedLegacyAsync(
        string connectionString,
        Guid accountId,
        string textBody)
    {
        {
            var context = CreateContext(connectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
            await (context.Inboxes.AddAsync(new InboxDB
            {
                Id = accountId,
                Name = "vacation",
                Address = address,
                Owner = user,
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
            await (context.JmapVacationResponses.AddAsync(new JmapVacationResponseDB
            {
                AccountId = accountId,
                IsEnabled = true,
                TextBody = textBody,
                HtmlBody = "<p>Away</p>",
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE jmap_vacation_responses
                    DROP COLUMN body_size_bytes,
                    DROP COLUMN body_object_provider,
                    DROP COLUMN body_object_name,
                    DROP COLUMN body_object_sha256,
                    DROP COLUMN body_object_etag;
                """).ConfigureAwait(false);
        }

        {
            var context = CreateContext(connectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
        }
        var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
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
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
