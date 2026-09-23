using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
public sealed class DistributedBackupExporterTests
{
    [TestMethod]
    public async Task ExportsMatchingDatabaseSnapshotAndAzureBlobContent()
    {
        var blobConnection = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blobConnection))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION.");
            return;
        }

        await using var sourceDatabase = await RequirePostgresAsync();
        await using var restoredDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var dataSource = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        var service = new BlobServiceClient(blobConnection);
        var container = service.GetBlobContainerClient($"mk8-export-{Guid.NewGuid():N}");
        var objects = new AzureBlobLargeObjectStore(
            service,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = container.Name,
                CreateContainerIfMissing = true,
            });
        var parent = Directory.CreateTempSubdirectory("mk8-distributed-export-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            var content = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 1);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            await using var upload = new MemoryStream(content, writable: false);
            var written = await objects.PutIfAbsentAsync(
                "jmap/exported", upload, content.LongLength, sha256,
                "application/octet-stream");
            var rowId = Guid.NewGuid();
            await InsertJmapBlobAsync(dataSource, rowId, written.Reference);

            var result = await DistributedBackupExporter.ExportAsync(
                dataSource, objects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);

            Assert.AreEqual(1L, result.ReferenceCount);
            Assert.AreEqual(1L, result.UniqueContentCount);
            CollectionAssert.AreEqual(
                content, await File.ReadAllBytesAsync(Path.Combine(destination, "blobs", sha256)));
            var manifest = await File.ReadAllLinesAsync(
                Path.Combine(destination, "references.jsonl"));
            Assert.HasCount(1, manifest);
            var referenceRow = JsonSerializer.Deserialize<DistributedBlobReferenceRow>(manifest[0]);
            Assert.IsNotNull(referenceRow);
            Assert.AreEqual(rowId, referenceRow.RowId);
            Assert.AreEqual("jmap_blobs.object_name", referenceRow.Source);
            Assert.AreEqual(written.Reference, referenceRow.Reference);
            var metadata = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(destination, "backup.json")));
            Assert.AreEqual(1, metadata.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.AreEqual(result.DatabaseSha256,
                metadata.RootElement.GetProperty("DatabaseSha256").GetString());
            await VerifyChecksumsAsync(destination);

            await RestoreDumpAsync(
                Path.Combine(destination, "database.dump"),
                restoredDatabase.ConnectionString);
            await using var restoredSource = NpgsqlDataSource.Create(
                restoredDatabase.ConnectionString);
            await using var restored = await restoredSource.OpenConnectionAsync();
            await using var query = restored.CreateCommand();
            query.CommandText = "SELECT object_name FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            Assert.AreEqual("jmap/exported", await query.ExecuteScalarAsync());
        }
        finally
        {
            await container.DeleteIfExistsAsync();
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotExportKeepsBlobWhenItsRowIsDeletedDuringCopy()
    {
        await using var sourceDatabase = await RequirePostgresAsync();
        await using var restoredDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var exportSource = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var applicationSource = NpgsqlDataSource.Create(
            sourceDatabase.ConnectionString);
        var raw = new InMemoryLargeObjectStore();
        var content = "snapshot-visible content"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var upload = new MemoryStream(content, writable: false);
        var written = await raw.PutIfAbsentAsync(
            "jmap/concurrent", upload, content.LongLength, sha256,
            "application/octet-stream");
        var rowId = Guid.NewGuid();
        await InsertJmapBlobAsync(exportSource, rowId, written.Reference);
        var blocking = new BlockingCopyStore(raw);
        var coordinated = new PostgresCoordinatedLargeObjectStore(applicationSource, raw);
        var parent = Directory.CreateTempSubdirectory("mk8-concurrent-export-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            var exporting = DistributedBackupExporter.ExportAsync(
                exportSource, blocking, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var connection = await applicationSource.OpenConnectionAsync())
            await using (var deleteRow = connection.CreateCommand())
            {
                deleteRow.CommandText = "DELETE FROM jmap_blobs WHERE id = @id";
                deleteRow.Parameters.AddWithValue("id", rowId);
                Assert.AreEqual(1, await deleteRow.ExecuteNonQueryAsync());
            }

            var deletingBlob = coordinated.DeleteIfMatchAsync(written.Reference);
            await WaitForAdvisoryWaitAsync(applicationSource);
            Assert.IsFalse(deletingBlob.IsCompleted);
            blocking.Release.TrySetResult(true);
            var result = await exporting.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1L, result.ReferenceCount);
            Assert.IsTrue(await deletingBlob.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, raw.ObjectCount);
            CollectionAssert.AreEqual(
                content, await File.ReadAllBytesAsync(
                    Path.Combine(destination, "blobs", sha256)));

            await RestoreDumpAsync(
                Path.Combine(destination, "database.dump"),
                restoredDatabase.ConnectionString);
            await using var restoredSource = NpgsqlDataSource.Create(
                restoredDatabase.ConnectionString);
            await using var restored = await restoredSource.OpenConnectionAsync();
            await using var query = restored.CreateCommand();
            query.CommandText = "SELECT count(*) FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            Assert.AreEqual(1L, await query.ExecuteScalarAsync());
        }
        finally
        {
            blocking.Release.TrySetResult(true);
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingBlobFailsWithoutPublishingOrLeavingAnIncompleteExport()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var objects = new InMemoryLargeObjectStore();
        var content = "missing from source store"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var upload = new MemoryStream(content, writable: false);
        var written = await objects.PutIfAbsentAsync(
            "jmap/missing", upload, content.LongLength, sha256,
            "application/octet-stream");
        await InsertJmapBlobAsync(dataSource, Guid.NewGuid(), written.Reference);
        objects.Remove(written.Reference.ObjectName);
        var parent = Directory.CreateTempSubdirectory("mk8-failed-export-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupExporter.ExportAsync(
                    dataSource, objects, database.ConnectionString,
                    destination, PgDumpExecutable));
            Assert.IsFalse(Path.Exists(destination));
            Assert.HasCount(0, Directory.GetFileSystemEntries(parent.FullName));
            await using var lease = await PostgresBlobDeletionBarrier
                .AcquireExclusiveAsync(dataSource).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    private static string PgDumpExecutable =>
        Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_PG_DUMP") ?? "pg_dump";

    private static async Task InsertJmapBlobAsync(
        NpgsqlDataSource source,
        Guid rowId,
        LargeObjectReference reference)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO jmap_blobs (
                id, blob_id, account_id, content_type, content,
                object_provider, object_name, object_sha256, object_etag,
                size_bytes, created_at, expires_at)
            VALUES (
                @id, @blob_id, @account_id, 'application/octet-stream',
                NULL, @provider, @name, @sha256, @etag,
                @size_bytes, now(), now() + interval '1 day')
            """;
        insert.Parameters.AddWithValue("id", rowId);
        insert.Parameters.AddWithValue("blob_id", $"export-{rowId:N}");
        insert.Parameters.AddWithValue("account_id", Guid.NewGuid());
        insert.Parameters.AddWithValue("provider", reference.Provider);
        insert.Parameters.AddWithValue("name", reference.ObjectName);
        insert.Parameters.AddWithValue("sha256", reference.Sha256);
        insert.Parameters.AddWithValue("etag", reference.EntityTag);
        insert.Parameters.AddWithValue("size_bytes", reference.Length);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task PrepareAsync(PostgresTestDatabase database)
    {
        await using var context = new EmailDbContext(
            new DbContextOptionsBuilder<EmailDbContext>()
                .UseNpgsql(database.ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        await new MailRuntimeSchemaService(context).EnsureAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
    }

    private static async Task VerifyChecksumsAsync(string destination)
    {
        foreach (var line in await File.ReadAllLinesAsync(
                     Path.Combine(destination, "SHA256SUMS")))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            Assert.IsTrue(separator > 0);
            var path = Path.Combine(destination, line[(separator + 2)..]);
            await using var input = File.OpenRead(path);
            Assert.AreEqual(
                line[..separator],
                Convert.ToHexStringLower(await SHA256.HashDataAsync(input)));
        }
    }

    private static async Task WaitForAdvisoryWaitAsync(NpgsqlDataSource source)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database()
                    AND wait_event_type = 'Lock'
                    AND wait_event = 'advisory'
                """;
            if ((long)(await command.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(20);
        }
        Assert.Fail("Physical Blob deletion did not wait for the backup lease.");
    }

    private static async Task RestoreDumpAsync(string dump, string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        var pgRestore = Path.GetDirectoryName(PgDumpExecutable) is { Length: > 0 } folder
            ? Path.Combine(folder, "pg_restore")
            : "pg_restore";
        var start = new ProcessStartInfo(pgRestore)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--exit-on-error");
        start.ArgumentList.Add("--no-owner");
        start.ArgumentList.Add("--no-acl");
        start.ArgumentList.Add("--no-password");
        start.ArgumentList.Add($"--host={database.Host}");
        start.ArgumentList.Add($"--port={database.Port}");
        start.ArgumentList.Add($"--username={database.Username}");
        start.ArgumentList.Add($"--dbname={database.Database}");
        start.ArgumentList.Add(dump);
        start.Environment["PGPASSWORD"] = database.Password;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("pg_restore did not start.");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            Assert.Fail($"pg_restore failed: {await error}");
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
        return database!;
    }

    private sealed class BlockingCopyStore(ILargeObjectStore inner) : ILargeObjectStore
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string Provider => inner.Provider;

        public Task<LargeObjectWriteResult> PutIfAbsentAsync(
            string objectName,
            Stream content,
            long length,
            string sha256,
            string contentType,
            CancellationToken cancellationToken = default) =>
            inner.PutIfAbsentAsync(
                objectName, content, length, sha256, contentType, cancellationToken);

        public async Task CopyToAsync(
            LargeObjectReference reference,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            await inner.CopyToAsync(reference, destination, cancellationToken);
        }

        public Task<bool> DeleteIfMatchAsync(
            LargeObjectReference reference,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfMatchAsync(reference, cancellationToken);
    }
}
