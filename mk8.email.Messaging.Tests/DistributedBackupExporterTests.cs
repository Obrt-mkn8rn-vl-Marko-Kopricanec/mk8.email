using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.Configuration;
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
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Distributed backup jobs require Linux.");
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
        await using var twiceRestoredDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var dataSource = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await DistributedRestoreActivationGuard.RequireReadyAsync(dataSource);
        var service = new BlobServiceClient(blobConnection);
        var container = service.GetBlobContainerClient($"mk8-export-{Guid.NewGuid():N}");
        var restoredContainer = service.GetBlobContainerClient(
            $"mk8-restored-{Guid.NewGuid():N}");
        var objects = new AzureBlobLargeObjectStore(
            service,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = container.Name,
                CreateContainerIfMissing = true,
            });
        var restoredObjects = new AzureBlobLargeObjectStore(
            service,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = restoredContainer.Name,
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
            var gatewayContent = "encrypted Gateway traffic"u8.ToArray();
            var gatewayHash = Convert.ToHexStringLower(SHA256.HashData(gatewayContent));
            await using var gatewayUpload = new MemoryStream(gatewayContent, writable: false);
            var gatewayWritten = await objects.PutIfAbsentAsync(
                "gateway/exported", gatewayUpload, gatewayContent.LongLength,
                gatewayHash, "application/vnd.mk8.encrypted-payload");
            var trafficId = Guid.NewGuid();
            await InsertGatewayTrafficAsync(dataSource, trafficId, gatewayWritten.Reference);

            var result = await DistributedBackupExporter.ExportAsync(
                dataSource, objects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);

            Assert.AreEqual(2L, result.ReferenceCount);
            Assert.AreEqual(2L, result.UniqueContentCount);
            CollectionAssert.AreEqual(
                content, await File.ReadAllBytesAsync(Path.Combine(destination, "blobs", sha256)));
            var manifest = await File.ReadAllLinesAsync(
                Path.Combine(destination, "references.jsonl"));
            Assert.HasCount(2, manifest);
            var referenceRow = manifest.Select(line =>
                    JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line))
                .Single(row => row?.RowId == rowId);
            Assert.IsNotNull(referenceRow);
            Assert.AreEqual(rowId, referenceRow.RowId);
            Assert.AreEqual("jmap_blobs.object_name", referenceRow.Source);
            Assert.AreEqual(written.Reference, referenceRow.Reference);
            Assert.AreEqual("application/octet-stream", referenceRow.ContentType);
            var gatewayRow = manifest.Select(line =>
                    JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line))
                .Single(row => row?.RowId == trafficId);
            Assert.IsNotNull(gatewayRow);
            Assert.AreEqual("gateway_traffic_records.payload_blob_name", gatewayRow.Source);
            Assert.AreEqual("application/vnd.mk8.encrypted-payload", gatewayRow.ContentType);
            var metadata = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(destination, "backup.json")));
            Assert.AreEqual(3, metadata.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.AreEqual(result.DatabaseSha256,
                metadata.RootElement.GetProperty("DatabaseSha256").GetString());
            await VerifyChecksumsAsync(destination);
            var verified = await DistributedBackupRestorer.VerifyAsync(destination);
            Assert.AreEqual(3, verified.SchemaVersion);
            Assert.AreEqual(2L, verified.ReferenceCount);
            Assert.AreEqual(2L, verified.UniqueContentCount);
            var verifiedCli = await RunVerifierCliAsync(destination);
            Assert.AreEqual(0, verifiedCli.ExitCode, verifiedCli.Output);
            StringAssert.Contains(verifiedCli.Output, "snapshot v3: 2 references");

            await using var restoredSource = NpgsqlDataSource.Create(
                restoredDatabase.ConnectionString);
            var restoredResult = await DistributedBackupRestorer.RestoreAsync(
                destination, restoredSource, restoredDatabase.ConnectionString,
                restoredObjects, PgRestoreExecutable);
            Assert.AreEqual(2L, restoredResult.ReferenceCount);
            Assert.AreEqual(2L, restoredResult.ImportedObjectCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(restoredSource);
            await using var restored = await restoredSource.OpenConnectionAsync();
            await using (var state = restored.CreateCommand())
            {
                state.CommandText = "SELECT state FROM public.mk8_restore_state WHERE id = 1";
                Assert.AreEqual("complete", await state.ExecuteScalarAsync());
            }
            await using var query = restored.CreateCommand();
            query.CommandText = "SELECT object_etag FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            var restoredEtag = (string)(await query.ExecuteScalarAsync())!;
            Assert.AreNotEqual(written.Reference.EntityTag, restoredEtag);
            await using var copied = new MemoryStream();
            await restoredObjects.CopyToAsync(
                written.Reference with { EntityTag = restoredEtag }, copied);
            CollectionAssert.AreEqual(content, copied.ToArray());
            await using var gatewayQuery = restored.CreateCommand();
            gatewayQuery.CommandText =
                "SELECT payload_blob_etag FROM gateway_traffic_records WHERE id = @id";
            gatewayQuery.Parameters.AddWithValue("id", trafficId);
            var restoredGatewayEtag = (string)(await gatewayQuery.ExecuteScalarAsync())!;
            await using var copiedGateway = new MemoryStream();
            await restoredObjects.CopyToAsync(
                gatewayWritten.Reference with { EntityTag = restoredGatewayEtag }, copiedGateway);
            CollectionAssert.AreEqual(gatewayContent, copiedGateway.ToArray());
            Assert.AreEqual(2L, await DistributedBlobReferenceAudit.AuditAsync(
                restoredSource, restoredObjects));

            var secondSnapshot = Path.Combine(parent.FullName, "restored-snapshot");
            var exportedAgain = await DistributedBackupExporter.ExportAsync(
                restoredSource, restoredObjects, restoredDatabase.ConnectionString,
                secondSnapshot, PgDumpExecutable);
            Assert.AreEqual(2L, exportedAgain.ReferenceCount);
            await using var twiceRestoredSource = NpgsqlDataSource.Create(
                twiceRestoredDatabase.ConnectionString);
            var twiceRestored = await DistributedBackupRestorer.RestoreAsync(
                secondSnapshot, twiceRestoredSource, twiceRestoredDatabase.ConnectionString,
                restoredObjects, PgRestoreExecutable);
            Assert.AreEqual(2L, twiceRestored.ReferenceCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(twiceRestoredSource);
            Assert.AreEqual(2L, await DistributedBlobReferenceAudit.AuditAsync(
                twiceRestoredSource, restoredObjects));

            var jobRoot = Path.Combine(parent.FullName, "job");
            Directory.CreateDirectory(jobRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var sourceConnection = new NpgsqlConnectionStringBuilder(sourceDatabase.ConnectionString);
            var jobConfig = new EnvironmentConfig
            {
                Database = new DatabaseConfig
                {
                    Host = sourceConnection.Host!,
                    Port = sourceConnection.Port,
                    Name = sourceConnection.Database!,
                    Username = sourceConnection.Username!,
                    Password = string.IsNullOrEmpty(sourceConnection.Password)
                        ? "local-test-only-password"
                        : sourceConnection.Password,
                },
                Smtp = new SmtpConfig
                {
                    Hostname = "email.example.test",
                    EnableSmtp = false,
                },
                Imap = new ImapConfig { EnableImap = false, EnableImplicitTls = false },
                Pop3 = new Pop3Config { EnablePop3 = false, EnableImplicitTls = false },
                Jmap = new JmapConfig { EnableJmap = false, IsDefault = false },
                Messaging = new MessagingConfig
                {
                    Enabled = true,
                    EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                },
                ObjectStorage = new ObjectStorageConfig
                {
                    ConnectionString = blobConnection,
                    ContainerName = container.Name,
                    CreateContainerIfMissing = true,
                },
            };
            Assert.HasCount(0, jobConfig.Validate(
                isDevelopment: false, EnvironmentValidationRole.ApplicationWorker));
            var jobConfigPath = Path.Combine(parent.FullName, "worker-config.json");
            await File.WriteAllTextAsync(jobConfigPath, JsonSerializer.Serialize(jobConfig));
            File.SetUnixFileMode(
                jobConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var job = await RunBackupJobAsync(jobConfigPath, jobRoot);
            Assert.AreEqual(0, job.ExitCode, job.Output);
            var published = Directory.GetDirectories(jobRoot).Single();
            var publishedSummary = await DistributedBackupRestorer.VerifyAsync(published);
            Assert.AreEqual(2L, publishedSummary.ReferenceCount);
            Assert.AreEqual(2L, publishedSummary.UniqueContentCount);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
            await restoredContainer.DeleteIfExistsAsync();
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task VersionTwoArchiveRestoresIntoGuardedDatabase()
    {
        await using var sourceDatabase = await RequirePostgresAsync();
        await using var targetDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        var objects = new InMemoryLargeObjectStore();
        var parent = Directory.CreateTempSubdirectory("mk8-version-two-restore-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, objects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);
            var metadataPath = Path.Combine(destination, "backup.json");
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath));
            Assert.IsNotNull(metadata);
            metadata["SchemaVersion"] = 2;
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
            await RewriteChecksumsAsync(destination);
            var verified = await RunVerifierCliAsync(destination);
            Assert.AreEqual(0, verified.ExitCode, verified.Output);
            StringAssert.Contains(verified.Output, "snapshot v2: 0 references");

            var result = await DistributedBackupRestorer.RestoreAsync(
                destination, target, targetDatabase.ConnectionString,
                objects, PgRestoreExecutable);
            Assert.AreEqual(0L, result.ReferenceCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(target);
        }
        finally
        {
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

    [TestMethod]
    public async Task RestoreRejectsCorruptArchiveAndNonemptyTargetBeforeBlobWrites()
    {
        await using var sourceDatabase = await RequirePostgresAsync();
        await using var targetDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        var originalObjects = new InMemoryLargeObjectStore();
        var targetObjects = new InMemoryLargeObjectStore();
        var content = "verified restore input"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var upload = new MemoryStream(content, writable: false);
        var written = await originalObjects.PutIfAbsentAsync(
            "jmap/restore-preflight", upload, content.LongLength, sha256,
            "application/octet-stream");
        await InsertJmapBlobAsync(source, Guid.NewGuid(), written.Reference);
        var parent = Directory.CreateTempSubdirectory("mk8-restore-preflight-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, originalObjects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);
            var blobFile = Path.Combine(destination, "blobs", sha256);
            await File.WriteAllBytesAsync(blobFile, "tampered"u8.ToArray());
            var invalidCli = await RunVerifierCliAsync(destination);
            Assert.AreEqual(1, invalidCli.ExitCode, invalidCli.Output);
            StringAssert.Contains(invalidCli.Output, "checksum mismatch");
            var corrupt = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable));
            StringAssert.Contains(corrupt.Message, "checksum mismatch");
            Assert.AreEqual(0, targetObjects.ObjectCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(target);
            await using (var marker = target.CreateCommand(
                             "SELECT to_regclass('public.mk8_restore_state') IS NULL"))
            {
                Assert.AreEqual(true, await marker.ExecuteScalarAsync());
            }

            await File.WriteAllBytesAsync(blobFile, content);
            await using (var connection = await target.OpenConnectionAsync())
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE existing_target_data (id integer PRIMARY KEY)";
                await create.ExecuteNonQueryAsync();
            }
            var nonempty = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable));
            StringAssert.Contains(nonempty.Message, "not empty");
            Assert.AreEqual(0, targetObjects.ObjectCount);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task BlobImportFailureLeavesPendingMarkerBeforeDatabaseRestore()
    {
        await using var sourceDatabase = await RequirePostgresAsync();
        await using var targetDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        var originalObjects = new InMemoryLargeObjectStore();
        var targetObjects = new InMemoryLargeObjectStore();
        var content = "expected restore content"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var upload = new MemoryStream(content, writable: false);
        var written = await originalObjects.PutIfAbsentAsync(
            "jmap/import-conflict", upload, content.LongLength, sha256,
            "application/octet-stream");
        await InsertJmapBlobAsync(source, Guid.NewGuid(), written.Reference);

        var conflicting = "different target content"u8.ToArray();
        var conflictingSha256 = Convert.ToHexStringLower(SHA256.HashData(conflicting));
        await using var conflictingUpload = new MemoryStream(conflicting, writable: false);
        await targetObjects.PutIfAbsentAsync(
            written.Reference.ObjectName, conflictingUpload, conflicting.LongLength,
            conflictingSha256, "application/octet-stream");

        var parent = Directory.CreateTempSubdirectory("mk8-import-failed-restore-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, originalObjects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable));
            StringAssert.Contains(failure.Message, "already uses that name");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedRestoreActivationGuard.RequireReadyAsync(target));
            await using var marker = target.CreateCommand(
                "SELECT state FROM public.mk8_restore_state");
            Assert.AreEqual("pending", await marker.ExecuteScalarAsync());
            await using var tables = target.CreateCommand(
                "SELECT to_regclass('public.jmap_blobs') IS NULL");
            Assert.AreEqual(true, await tables.ExecuteScalarAsync());
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task RestoreRejectsManifestThatDoesNotMatchTheDumpBeforeRebinding()
    {
        await using var sourceDatabase = await RequirePostgresAsync();
        await using var targetDatabase = await RequirePostgresAsync();
        await PrepareAsync(sourceDatabase);
        await using var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        var originalObjects = new InMemoryLargeObjectStore();
        var targetObjects = new InMemoryLargeObjectStore();
        var content = "manifest mismatch sentinel"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var upload = new MemoryStream(content, writable: false);
        var written = await originalObjects.PutIfAbsentAsync(
            "jmap/mismatched", upload, content.LongLength, sha256,
            "application/octet-stream");
        var rowId = Guid.NewGuid();
        await InsertJmapBlobAsync(source, rowId, written.Reference);
        var parent = Directory.CreateTempSubdirectory("mk8-mismatch-restore-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, originalObjects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable);
            var manifestPath = Path.Combine(destination, "references.jsonl");
            var original = JsonSerializer.Deserialize<DistributedBlobReferenceRow>(
                await File.ReadAllTextAsync(manifestPath));
            Assert.IsNotNull(original);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(original with { RowId = Guid.NewGuid() }) + "\n");
            var metadataPath = Path.Combine(destination, "backup.json");
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath));
            Assert.IsNotNull(metadata);
            await using (var input = File.OpenRead(manifestPath))
            {
                metadata["ManifestSha256"] =
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
            }
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
            await RewriteChecksumsAsync(destination);

            var mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable));
            StringAssert.Contains(mismatch.Message, "do not match");
            var blocked = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedRestoreActivationGuard.RequireReadyAsync(target));
            StringAssert.Contains(blocked.Message, "incomplete");
            var invalidSnapshot = Path.Combine(parent.FullName, "incomplete-source-snapshot");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupExporter.ExportAsync(
                    target, targetObjects, targetDatabase.ConnectionString,
                    invalidSnapshot, PgDumpExecutable));
            Assert.IsFalse(Path.Exists(invalidSnapshot));
            await using var restored = await target.OpenConnectionAsync();
            await using (var state = restored.CreateCommand())
            {
                state.CommandText = "SELECT state FROM public.mk8_restore_state WHERE id = 1";
                Assert.AreEqual("pending", await state.ExecuteScalarAsync());
            }
            await using var query = restored.CreateCommand();
            query.CommandText = "SELECT object_etag FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            Assert.AreEqual(written.Reference.EntityTag, await query.ExecuteScalarAsync());
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    private static string PgDumpExecutable =>
        Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_PG_DUMP") ?? "pg_dump";

    private static async Task<(int ExitCode, string Output)> RunBackupJobAsync(
        string configPath,
        string backupRoot)
    {
        var host = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? "dotnet";
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is missing.");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var assembly = Path.Combine(
            repository, "mk8.email.CLI", "bin", configuration,
            "net10.0", "mk8.email.Application.CLI.dll");
        var script = Path.Combine(repository, "deploy", "scripts", "mk8-distributed-backup");
        Assert.IsTrue(File.Exists(assembly), "The management CLI executable is missing.");
        Assert.IsTrue(File.Exists(script), "The distributed backup wrapper is missing.");
        var start = new ProcessStartInfo(script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(configPath);
        start.ArgumentList.Add(backupRoot);
        start.ArgumentList.Add(host);
        start.ArgumentList.Add(assembly);
        start.Environment["MK8EMAIL_PG_DUMP_EXECUTABLE"] = PgDumpExecutable;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The distributed backup job did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("The distributed backup job exceeded its deadline.");
        }
        return (process.ExitCode, await output + await error);
    }

    private static async Task<(int ExitCode, string Output)> RunVerifierCliAsync(
        string backupDirectory)
    {
        var host = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? "dotnet";
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is missing.");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var assembly = Path.Combine(
            repository, "mk8.email.CLI", "bin", configuration,
            "net10.0", "mk8.email.Application.CLI.dll");
        Assert.IsTrue(File.Exists(assembly), "The management CLI executable is missing.");
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--verify-distributed-snapshot");
        start.ArgumentList.Add(backupDirectory);
        start.Environment.Remove("MK8EMAIL_CONFIG_FILE");
        start.Environment.Remove("MK8_EMAIL_TEST_POSTGRES");
        start.Environment.Remove("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The management CLI did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("The archive verification CLI exceeded its deadline.");
        }
        return (process.ExitCode, await output + await error);
    }

    private static string PgRestoreExecutable =>
        Path.GetDirectoryName(PgDumpExecutable) is { Length: > 0 } folder
            ? Path.Combine(folder, "pg_restore")
            : "pg_restore";

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

    private static async Task InsertGatewayTrafficAsync(
        NpgsqlDataSource source,
        Guid rowId,
        LargeObjectReference reference)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO gateway_traffic_records (
                id, session_id, sequence, direction, protocol, content_type,
                encryption_key_id, payload_blob_provider, payload_blob_name,
                payload_blob_etag, payload_length, payload_nonce, payload_tag,
                payload_sha256, metadata, recorded_at)
            VALUES (
                @id, @session_id, 1, 'inbound', 'smtp', 'text/plain',
                'test-key', @provider, @name, @etag, @size_bytes,
                decode(repeat('00', 12), 'hex'),
                decode(repeat('00', 16), 'hex'),
                @sha256, '{}'::jsonb, now())
            """;
        insert.Parameters.AddWithValue("id", rowId);
        insert.Parameters.AddWithValue("session_id", Guid.NewGuid());
        insert.Parameters.AddWithValue("provider", reference.Provider);
        insert.Parameters.AddWithValue("name", reference.ObjectName);
        insert.Parameters.AddWithValue("etag", reference.EntityTag);
        insert.Parameters.AddWithValue("size_bytes", reference.Length);
        insert.Parameters.AddWithValue("sha256", reference.Sha256);
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

    private static async Task RewriteChecksumsAsync(string destination)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     destination, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "SHA256SUMS")
                continue;
            await using var input = File.OpenRead(file);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
            lines.Add($"{sha256}  {Path.GetRelativePath(destination, file)}");
        }
        await File.WriteAllLinesAsync(Path.Combine(destination, "SHA256SUMS"), lines);
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
        var start = new ProcessStartInfo(PgRestoreExecutable)
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
