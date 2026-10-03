using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DistributedBlobReferenceInventoryTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EnumeratesMessagingAndMailReferencesFromOneDatabaseSnapshot scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EnumeratesMessagingAndMailReferencesFromOneDatabaseSnapshot()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        var connection = (await dataSource.OpenConnectionAsync().ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var transaction = (await connection.BeginTransactionAsync().ConfigureAwait(false));
        await using var transactionLifetime = transaction.ConfigureAwait(false);

        var gatewayId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        {
            var insert = connection.CreateCommand();
            await using var insertLifetime = insert.ConfigureAwait(false);
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO gateway_traffic_records (
                    id, session_id, sequence, direction, protocol, content_type,
                    encryption_key_id, payload_blob_provider, payload_blob_name,
                    payload_blob_etag, payload_length, payload_nonce, payload_tag,
                    payload_sha256, metadata, recorded_at)
                VALUES (
                    @gateway_id, @session_id, 1, 'inbound', 'smtp',
                    'application/octet-stream', 'test-key', 'azure-blob',
                    'gateway/test', 'gateway-etag', 19,
                    decode(repeat('00', 12), 'hex'),
                    decode(repeat('00', 16), 'hex'),
                    repeat('a', 64), '{}'::jsonb, now());

                INSERT INTO jmap_blobs (
                    id, blob_id, account_id, content_type, content,
                    object_provider, object_name, object_sha256, object_etag,
                    size_bytes, created_at, expires_at)
                VALUES (
                    @blob_id, 'test-blob-id', @account_id,
                    'application/octet-stream', NULL,
                    'azure-blob', 'jmap/test', repeat('b', 64), 'jmap-etag',
                    31, now(), now() + interval '1 day');
                """;
            insert.Parameters.AddWithValue("gateway_id", gatewayId);
            insert.Parameters.AddWithValue("session_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("blob_id", blobId);
            insert.Parameters.AddWithValue("account_id", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var rows = new List<DistributedBlobReferenceRow>();
        await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                           connection, transaction).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        Assert.HasCount(2, rows);
        var gateway = rows.Single(row => row.RowId == gatewayId);
        Assert.AreEqual("gateway_traffic_records.payload_blob_name", gateway.Source, StringComparer.Ordinal);
        Assert.AreEqual("gateway/test", gateway.Reference.ObjectName, StringComparer.Ordinal);
        Assert.AreEqual(19L, gateway.Reference.Length);
        var jmap = rows.Single(row => row.RowId == blobId);
        Assert.AreEqual("jmap_blobs.object_name", jmap.Source, StringComparer.Ordinal);
        Assert.AreEqual("jmap/test", jmap.Reference.ObjectName, StringComparer.Ordinal);
        Assert.AreEqual("jmap-etag", jmap.Reference.EntityTag, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task RejectsUnknownReferenceColumnInsteadOfSilentlyOmittingItsObjects()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        var connection = (await dataSource.OpenConnectionAsync().ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var transaction = (await connection.BeginTransactionAsync().ConfigureAwait(false));
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        {
            var alter = connection.CreateCommand();
            await using var alterLifetime = alter.ConfigureAwait(false);
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE jmap_blobs ADD COLUMN media_object_name text";
            await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedBlobReferenceInventory.ValidateSchemaAsync(connection, transaction)).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "jmap_blobs.media_object_name", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RejectsIncompleteLegacyReferenceInsteadOfTreatingItAsInlineData()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        var connection = (await dataSource.OpenConnectionAsync().ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var transaction = (await connection.BeginTransactionAsync().ConfigureAwait(false));
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var id = Guid.NewGuid();
        {
            var insert = connection.CreateCommand();
            await using var insertLifetime = insert.ConfigureAwait(false);
            insert.Transaction = transaction;
            insert.CommandText = """
                ALTER TABLE jmap_blobs DROP CONSTRAINT ck_jmap_blobs_storage_shape;
                INSERT INTO jmap_blobs (
                    id, blob_id, account_id, content_type, content,
                    object_provider, size_bytes, created_at, expires_at)
                VALUES (
                    @id, 'incomplete-blob', @account_id,
                    'application/octet-stream', NULL, 'azure-blob',
                    3, now(), now() + interval '1 day');
                """;
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("account_id", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in DistributedBlobReferenceInventory.EnumerateAsync(
                               connection, transaction).ConfigureAwait(false))
            {
            }
        }).ConfigureAwait(false);
        StringAssert.Contains(error.Message, id.ToString("D"), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AuditDetectsChangedAndMissingReferencedContent()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        var objects = new InMemoryLargeObjectStore();
        var content = "a referenced JMAP attachment"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var payload = new MemoryStream(content, writable: false);
        await using var payloadLifetime = payload.ConfigureAwait(false);
        var written = await objects.PutIfAbsentAsync(
            "jmap/audited", payload, content.LongLength, sha256,
            "application/octet-stream").ConfigureAwait(false);

        {
            var connection = (await dataSource.OpenConnectionAsync().ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            var insert = connection.CreateCommand();
            await using var insertLifetime = insert.ConfigureAwait(false);
            insert.CommandText = """
                INSERT INTO jmap_blobs (
                    id, blob_id, account_id, content_type, content,
                    object_provider, object_name, object_sha256, object_etag,
                    size_bytes, created_at, expires_at)
                VALUES (
                    @id, 'audited-blob', @account_id, 'application/octet-stream',
                    NULL, @provider, @name, @sha256, @etag,
                    @size_bytes, now(), now() + interval '1 day')
                """;
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("account_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("provider", written.Reference.Provider);
            insert.Parameters.AddWithValue("name", written.Reference.ObjectName);
            insert.Parameters.AddWithValue("sha256", written.Reference.Sha256);
            insert.Parameters.AddWithValue("etag", written.Reference.EntityTag);
            insert.Parameters.AddWithValue("size_bytes", written.Reference.Length);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Assert.AreEqual(1L, await DistributedBlobReferenceAudit.AuditAsync(dataSource, objects).ConfigureAwait(false));
        objects.Corrupt(written.Reference.ObjectName);
        var corrupted = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedBlobReferenceAudit.AuditAsync(dataSource, objects)).ConfigureAwait(false);
        StringAssert.Contains(corrupted.Message, "content hash mismatch", StringComparison.Ordinal);
        objects.Remove(written.Reference.ObjectName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedBlobReferenceAudit.AuditAsync(dataSource, objects)).ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The AuditStreamsReferencedContentFromAzureBlobCompatibleEndpoint scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task AuditStreamsReferencedContentFromAzureBlobCompatibleEndpoint()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION.");
            return;
        }

        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient($"mk8-reference-audit-{Guid.NewGuid():N}");
        var objects = new AzureBlobLargeObjectStore(
            service,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = container.Name,
                CreateContainerIfMissing = true,
            });
        try
        {
            var content = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 1);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            var payload = new MemoryStream(content, writable: false);
            await using var payloadLifetime = payload.ConfigureAwait(false);
            var written = await objects.PutIfAbsentAsync(
                "dav/audited", payload, content.LongLength, sha256,
                "text/plain").ConfigureAwait(false);
            {
                var connection = (await dataSource.OpenConnectionAsync().ConfigureAwait(false));
                await using var connectionLifetime = connection.ConfigureAwait(false);
                var insert = connection.CreateCommand();
                await using var insertLifetime = insert.ConfigureAwait(false);
                insert.CommandText = """
                    INSERT INTO jmap_blobs (
                        id, blob_id, account_id, content_type, content,
                        object_provider, object_name, object_sha256, object_etag,
                        size_bytes, created_at, expires_at)
                    VALUES (
                        @id, 'protocol-audited-blob', @account_id, 'text/plain',
                        NULL, @provider, @name, @sha256, @etag,
                        @size_bytes, now(), now() + interval '1 day')
                    """;
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("account_id", Guid.NewGuid());
                insert.Parameters.AddWithValue("provider", written.Reference.Provider);
                insert.Parameters.AddWithValue("name", written.Reference.ObjectName);
                insert.Parameters.AddWithValue("sha256", written.Reference.Sha256);
                insert.Parameters.AddWithValue("etag", written.Reference.EntityTag);
                insert.Parameters.AddWithValue("size_bytes", written.Reference.Length);
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            Assert.AreEqual(1L, await DistributedBlobReferenceAudit.AuditAsync(dataSource, objects).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    private static async Task PrepareAsync(PostgresTestDatabase testDatabase)
    {
        var context = new EmailDbContext(
            new DbContextOptionsBuilder<EmailDbContext>()
                .UseNpgsql(testDatabase.ConnectionString).Options);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(testDatabase.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
        return database!;
    }
}
