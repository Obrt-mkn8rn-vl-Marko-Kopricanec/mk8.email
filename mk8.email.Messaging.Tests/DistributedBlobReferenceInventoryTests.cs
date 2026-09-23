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
public sealed class DistributedBlobReferenceInventoryTests
{
    [TestMethod]
    public async Task EnumeratesMessagingAndMailReferencesFromOneDatabaseSnapshot()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var gatewayId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
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
            await insert.ExecuteNonQueryAsync();
        }

        var rows = new List<DistributedBlobReferenceRow>();
        await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                           connection, transaction))
        {
            rows.Add(row);
        }

        Assert.HasCount(2, rows);
        var gateway = rows.Single(row => row.RowId == gatewayId);
        Assert.AreEqual("gateway_traffic_records.payload_blob_name", gateway.Source);
        Assert.AreEqual("gateway/test", gateway.Reference.ObjectName);
        Assert.AreEqual(19L, gateway.Reference.Length);
        var jmap = rows.Single(row => row.RowId == blobId);
        Assert.AreEqual("jmap_blobs.object_name", jmap.Source);
        Assert.AreEqual("jmap/test", jmap.Reference.ObjectName);
        Assert.AreEqual("jmap-etag", jmap.Reference.EntityTag);
    }

    [TestMethod]
    public async Task RejectsUnknownReferenceColumnInsteadOfSilentlyOmittingItsObjects()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var alter = connection.CreateCommand())
        {
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE jmap_blobs ADD COLUMN media_object_name text";
            await alter.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedBlobReferenceInventory.ValidateSchemaAsync(connection, transaction));
        StringAssert.Contains(exception.Message, "jmap_blobs.media_object_name");
    }

    [TestMethod]
    public async Task RejectsIncompleteLegacyReferenceInsteadOfTreatingItAsInlineData()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
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
            await insert.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in DistributedBlobReferenceInventory.EnumerateAsync(
                               connection, transaction))
            {
            }
        });
        StringAssert.Contains(error.Message, id.ToString("D"));
    }

    [TestMethod]
    public async Task AuditDetectsChangedAndMissingReferencedContent()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var objects = new InMemoryLargeObjectStore();
        var content = "a referenced JMAP attachment"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var payload = new MemoryStream(content, writable: false);
        var written = await objects.PutIfAbsentAsync(
            "jmap/audited", payload, content.LongLength, sha256,
            "application/octet-stream");

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
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
            await insert.ExecuteNonQueryAsync();
        }

        Assert.AreEqual(1L, await DistributedBlobReferenceAudit.AuditAsync(dataSource, objects));
        objects.Corrupt(written.Reference.ObjectName);
        var corrupted = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedBlobReferenceAudit.AuditAsync(dataSource, objects));
        StringAssert.Contains(corrupted.Message, "content hash mismatch");
        objects.Remove(written.Reference.ObjectName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedBlobReferenceAudit.AuditAsync(dataSource, objects));
    }

    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
    public async Task AuditStreamsReferencedContentFromAzureBlobCompatibleEndpoint()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION.");
            return;
        }

        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
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
            await using var payload = new MemoryStream(content, writable: false);
            var written = await objects.PutIfAbsentAsync(
                "dav/audited", payload, content.LongLength, sha256,
                "text/plain");
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var insert = connection.CreateCommand())
            {
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
                await insert.ExecuteNonQueryAsync();
            }

            Assert.AreEqual(1L, await DistributedBlobReferenceAudit.AuditAsync(dataSource, objects));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static async Task PrepareAsync(PostgresTestDatabase testDatabase)
    {
        await using var context = new EmailDbContext(
            new DbContextOptionsBuilder<EmailDbContext>()
                .UseNpgsql(testDatabase.ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        await new MailRuntimeSchemaService(context).EnsureAsync();
        await using var dataSource = NpgsqlDataSource.Create(testDatabase.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
        return database!;
    }
}
