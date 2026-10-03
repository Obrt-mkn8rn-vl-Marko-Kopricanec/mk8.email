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
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DistributedBackupExporterTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ExportsMatchingDatabaseSnapshotAndAzureBlobContent scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
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

        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var restoredDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var restoredDatabaseLifetime = restoredDatabase.ConfigureAwait(false);
        var twiceRestoredDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var twiceRestoredDatabaseLifetime = twiceRestoredDatabase.ConfigureAwait(false);
        await PrepareAsync(sourceDatabase).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await DistributedRestoreActivationGuard.RequireReadyAsync(dataSource).ConfigureAwait(false);
        var service = new BlobServiceClient(blobConnection);
        var container = service.GetBlobContainerClient($"mk8-export-{Guid.NewGuid():N}");
        var restoredContainer = service.GetBlobContainerClient(
            $"mk8-restored-{Guid.NewGuid():N}");
        var archiveContainer = service.GetBlobContainerClient(
            $"mk8-archive-{Guid.NewGuid():N}");
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
            var upload = new MemoryStream(content, writable: false);
            await using var uploadLifetime = upload.ConfigureAwait(false);
            var written = await objects.PutIfAbsentAsync(
                "jmap/exported", upload, content.LongLength, sha256,
                "application/octet-stream").ConfigureAwait(false);
            var rowId = Guid.NewGuid();
            await InsertJmapBlobAsync(dataSource, rowId, written.Reference).ConfigureAwait(false);
            var gatewayContent = "encrypted Gateway traffic"u8.ToArray();
            var gatewayHash = Convert.ToHexStringLower(SHA256.HashData(gatewayContent));
            var gatewayUpload = new MemoryStream(gatewayContent, writable: false);
            await using var gatewayUploadLifetime = gatewayUpload.ConfigureAwait(false);
            var gatewayWritten = await objects.PutIfAbsentAsync(
                "gateway/exported", gatewayUpload, gatewayContent.LongLength,
                gatewayHash, "application/vnd.mk8.encrypted-payload").ConfigureAwait(false);
            var trafficId = Guid.NewGuid();
            await InsertGatewayTrafficAsync(dataSource, trafficId, gatewayWritten.Reference).ConfigureAwait(false);

            var result = await DistributedBackupExporter.ExportAsync(
                dataSource, objects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable).ConfigureAwait(false);

            Assert.AreEqual(2L, result.ReferenceCount);
            Assert.AreEqual(2L, result.UniqueContentCount);
            CollectionAssert.AreEqual(
                content, await File.ReadAllBytesAsync(Path.Combine(destination, "blobs", sha256)).ConfigureAwait(false));
            var manifest = await File.ReadAllLinesAsync(
                Path.Combine(destination, "references.jsonl")).ConfigureAwait(false);
            Assert.HasCount(2, manifest);
            var referenceRow = manifest.Select(line =>
                    JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line))
                .Single(row => row?.RowId == rowId);
            Assert.IsNotNull(referenceRow);
            Assert.AreEqual(rowId, referenceRow.RowId);
            Assert.AreEqual("jmap_blobs.object_name", referenceRow.Source, StringComparer.Ordinal);
            Assert.AreEqual(written.Reference, referenceRow.Reference);
            Assert.AreEqual("application/octet-stream", referenceRow.ContentType, StringComparer.Ordinal);
            var gatewayRow = manifest.Select(line =>
                    JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line))
                .Single(row => row?.RowId == trafficId);
            Assert.IsNotNull(gatewayRow);
            Assert.AreEqual("gateway_traffic_records.payload_blob_name", gatewayRow.Source, StringComparer.Ordinal);
            Assert.AreEqual("application/vnd.mk8.encrypted-payload", gatewayRow.ContentType, StringComparer.Ordinal);
            var metadata = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(destination, "backup.json")).ConfigureAwait(false));
            Assert.AreEqual(3, metadata.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.AreEqual(result.DatabaseSha256,
                metadata.RootElement.GetProperty("DatabaseSha256").GetString(), StringComparer.Ordinal);
            await VerifyChecksumsAsync(destination).ConfigureAwait(false);
            var verified = await DistributedBackupRestorer.VerifyAsync(destination).ConfigureAwait(false);
            Assert.AreEqual(3, verified.SchemaVersion);
            Assert.AreEqual(2L, verified.ReferenceCount);
            Assert.AreEqual(2L, verified.UniqueContentCount);
            var verifiedCli = await RunVerifierCliAsync(destination).ConfigureAwait(false);
            Assert.AreEqual(0, verifiedCli.ExitCode, verifiedCli.Output);
            StringAssert.Contains(verifiedCli.Output, "snapshot v3: 2 references", StringComparison.Ordinal);

            var restoredSource = NpgsqlDataSource.Create(
                restoredDatabase.ConnectionString);
            await using var restoredSourceLifetime = restoredSource.ConfigureAwait(false);
            var restoredResult = await DistributedBackupRestorer.RestoreAsync(
                destination, restoredSource, restoredDatabase.ConnectionString,
                restoredObjects, PgRestoreExecutable).ConfigureAwait(false);
            Assert.AreEqual(2L, restoredResult.ReferenceCount);
            Assert.AreEqual(2L, restoredResult.ImportedObjectCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(restoredSource).ConfigureAwait(false);
            var restored = (await restoredSource.OpenConnectionAsync().ConfigureAwait(false));
            await using var restoredLifetime = restored.ConfigureAwait(false);
            {
                var state = restored.CreateCommand();
                await using var stateLifetime = state.ConfigureAwait(false);
                state.CommandText = "SELECT state FROM public.mk8_restore_state WHERE id = 1";
                Assert.AreEqual("complete", await state.ExecuteScalarAsync().ConfigureAwait(false));
            }
            var query = restored.CreateCommand();
            await using var queryLifetime = query.ConfigureAwait(false);
            query.CommandText = "SELECT object_etag FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            var restoredEtag = (string)(await query.ExecuteScalarAsync().ConfigureAwait(false))!;
            Assert.AreNotEqual(written.Reference.EntityTag, restoredEtag, StringComparer.Ordinal);
            var copied = new MemoryStream();
            await using var copiedLifetime = copied.ConfigureAwait(false);
            await restoredObjects.CopyToAsync(
                written.Reference with { EntityTag = restoredEtag }, copied).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, copied.ToArray());
            var gatewayQuery = restored.CreateCommand();
            await using var gatewayQueryLifetime = gatewayQuery.ConfigureAwait(false);
            gatewayQuery.CommandText =
                "SELECT payload_blob_etag FROM gateway_traffic_records WHERE id = @id";
            gatewayQuery.Parameters.AddWithValue("id", trafficId);
            var restoredGatewayEtag = (string)(await gatewayQuery.ExecuteScalarAsync().ConfigureAwait(false))!;
            var copiedGateway = new MemoryStream();
            await using var copiedGatewayLifetime = copiedGateway.ConfigureAwait(false);
            await restoredObjects.CopyToAsync(
                gatewayWritten.Reference with { EntityTag = restoredGatewayEtag }, copiedGateway).ConfigureAwait(false);
            CollectionAssert.AreEqual(gatewayContent, copiedGateway.ToArray());
            Assert.AreEqual(2L, await DistributedBlobReferenceAudit.AuditAsync(
                restoredSource, restoredObjects).ConfigureAwait(false));

            var secondSnapshot = Path.Combine(parent.FullName, "restored-snapshot");
            var exportedAgain = await DistributedBackupExporter.ExportAsync(
                restoredSource, restoredObjects, restoredDatabase.ConnectionString,
                secondSnapshot, PgDumpExecutable).ConfigureAwait(false);
            Assert.AreEqual(2L, exportedAgain.ReferenceCount);
            var twiceRestoredSource = NpgsqlDataSource.Create(
                twiceRestoredDatabase.ConnectionString);
            await using var twiceRestoredSourceLifetime = twiceRestoredSource.ConfigureAwait(false);
            var twiceRestored = await DistributedBackupRestorer.RestoreAsync(
                secondSnapshot, twiceRestoredSource, twiceRestoredDatabase.ConnectionString,
                restoredObjects, PgRestoreExecutable).ConfigureAwait(false);
            Assert.AreEqual(2L, twiceRestored.ReferenceCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(twiceRestoredSource).ConfigureAwait(false);
            Assert.AreEqual(2L, await DistributedBlobReferenceAudit.AuditAsync(
                twiceRestoredSource, restoredObjects).ConfigureAwait(false));

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
            await File.WriteAllTextAsync(jobConfigPath, JsonSerializer.Serialize(jobConfig)).ConfigureAwait(false);
            File.SetUnixFileMode(
                jobConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var job = await RunBackupJobAsync(jobConfigPath, jobRoot).ConfigureAwait(false);
            Assert.AreEqual(0, job.ExitCode, job.Output);
            var published = Directory.GetDirectories(jobRoot).Single();
            var publishedSummary = await DistributedBackupRestorer.VerifyAsync(published).ConfigureAwait(false);
            Assert.AreEqual(2L, publishedSummary.ReferenceCount);
            Assert.AreEqual(2L, publishedSummary.UniqueContentCount);

            var identity = Path.Combine(jobRoot, "age-identity.txt");
            var recipients = Path.Combine(jobRoot, "age-recipients.txt");
            var generated = await RunExternalAsync("/usr/bin/age-keygen", ["-o", identity]).ConfigureAwait(false);
            Assert.AreEqual(0, generated.ExitCode, generated.Output);
            var publicRecipient = await RunExternalAsync("/usr/bin/age-keygen", ["-y", identity]).ConfigureAwait(false);
            Assert.AreEqual(0, publicRecipient.ExitCode, publicRecipient.Output);
            await File.WriteAllTextAsync(recipients, publicRecipient.Output.Trim() + "\n").ConfigureAwait(false);
            File.SetUnixFileMode(recipients, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (var signer = RSA.Create(3072))
            {
                var signingKey = Path.Combine(jobRoot, "archive-signing.pem");
                var verifyKey = Path.Combine(jobRoot, "archive-verify.pem");
                await File.WriteAllTextAsync(signingKey, signer.ExportPkcs8PrivateKeyPem()).ConfigureAwait(false);
                await File.WriteAllTextAsync(verifyKey, signer.ExportSubjectPublicKeyInfoPem()).ConfigureAwait(false);
                File.SetUnixFileMode(signingKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(verifyKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var sealedPath = Path.Combine(jobRoot, "sealed");
                var recoveredPath = Path.Combine(jobRoot, "recovered");
                var sealedResult = await RunArchiveAsync(
                    ["seal", published, recipients, signingKey, verifyKey, sealedPath]).ConfigureAwait(false);
                Assert.AreEqual(0, sealedResult.ExitCode, sealedResult.Output);
                var recoveredResult = await RunArchiveAsync(
                    ["unseal", sealedPath, identity, verifyKey, recoveredPath]).ConfigureAwait(false);
                Assert.AreEqual(0, recoveredResult.ExitCode, recoveredResult.Output);
                var recoveredSummary = await DistributedBackupRestorer.VerifyAsync(recoveredPath).ConfigureAwait(false);
                Assert.AreEqual(2L, recoveredSummary.ReferenceCount);
                CollectionAssert.AreEqual(
                    await File.ReadAllBytesAsync(Path.Combine(published, "blobs", sha256)).ConfigureAwait(false),
                    await File.ReadAllBytesAsync(Path.Combine(recoveredPath, "blobs", sha256)).ConfigureAwait(false));

                var connectionFile = Path.Combine(jobRoot, "archive-blob-connection.txt");
                await File.WriteAllTextAsync(connectionFile, blobConnection + "\n").ConfigureAwait(false);
                File.SetUnixFileMode(connectionFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var archiveId = "snapshot-" + Guid.NewGuid().ToString("N");
                var publishArguments = new[]
                {
                    "--publish-distributed-archive", connectionFile,
                    archiveContainer.Name, archiveId, sealedPath, verifyKey,
                };
                var remotePublish = await RunArchiveTransportCliAsync(publishArguments).ConfigureAwait(false);
                Assert.AreEqual(0, remotePublish.ExitCode, remotePublish.Output);
                var retryPublish = await RunArchiveTransportCliAsync(publishArguments).ConfigureAwait(false);
                Assert.AreEqual(0, retryPublish.ExitCode, retryPublish.Output);
                var remoteEntries = new List<string>();
                await foreach (var blob in archiveContainer.GetBlobsAsync().ConfigureAwait(false))
                    remoteEntries.Add(blob.Name);
                Assert.HasCount(3, remoteEntries);
                var completion = archiveContainer.GetBlobClient(
                    $"distributed-archives/v1/{archiveId}/complete.json");
                await completion.DeleteAsync().ConfigureAwait(false);
                var incomplete = Path.Combine(jobRoot, "incomplete-download");
                var incompleteFetch = await RunArchiveTransportCliAsync(
                    ["--fetch-distributed-archive", connectionFile,
                        archiveContainer.Name, archiveId, incomplete, verifyKey]).ConfigureAwait(false);
                Assert.AreNotEqual(0, incompleteFetch.ExitCode);
                Assert.IsFalse(Path.Exists(incomplete));
                var republish = await RunArchiveTransportCliAsync(publishArguments).ConfigureAwait(false);
                Assert.AreEqual(0, republish.ExitCode, republish.Output);

                File.SetUnixFileMode(connectionFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                var exposedConnection = await RunArchiveTransportCliAsync(publishArguments).ConfigureAwait(false);
                Assert.AreNotEqual(0, exposedConnection.ExitCode);
                File.SetUnixFileMode(connectionFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var hardLink = Path.Combine(jobRoot, "linked-verify.pem");
                var linkResult = await RunExternalAsync("/usr/bin/ln", [verifyKey, hardLink]).ConfigureAwait(false);
                Assert.AreEqual(0, linkResult.ExitCode, linkResult.Output);
                var linkedKeyDestination = Path.Combine(jobRoot, "linked-key-download");
                var linkedKeyFetch = await RunArchiveTransportCliAsync(
                    ["--fetch-distributed-archive", connectionFile,
                        archiveContainer.Name, archiveId, linkedKeyDestination, verifyKey]).ConfigureAwait(false);
                Assert.AreNotEqual(0, linkedKeyFetch.ExitCode);
                Assert.IsFalse(Path.Exists(linkedKeyDestination));
                File.Delete(hardLink);

                using (var unrelatedSigner = RSA.Create(3072))
                {
                    var unrelatedKey = Path.Combine(jobRoot, "unrelated-verify.pem");
                    await File.WriteAllTextAsync(
                        unrelatedKey, unrelatedSigner.ExportSubjectPublicKeyInfoPem()).ConfigureAwait(false);
                    File.SetUnixFileMode(unrelatedKey,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    var wrongKeyDestination = Path.Combine(jobRoot, "wrong-key-download");
                    var wrongKeyFetch = await RunArchiveTransportCliAsync(
                        ["--fetch-distributed-archive", connectionFile,
                            archiveContainer.Name, archiveId, wrongKeyDestination, unrelatedKey]).ConfigureAwait(false);
                    Assert.AreNotEqual(0, wrongKeyFetch.ExitCode);
                    Assert.IsFalse(Path.Exists(wrongKeyDestination));
                }
                var downloaded = Path.Combine(jobRoot, "downloaded-sealed");
                var remoteFetch = await RunArchiveTransportCliAsync(
                    ["--fetch-distributed-archive", connectionFile,
                        archiveContainer.Name, archiveId, downloaded, verifyKey]).ConfigureAwait(false);
                Assert.AreEqual(0, remoteFetch.ExitCode, remoteFetch.Output);
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(downloaded));
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(downloaded, "snapshot.tar.age")));
                CollectionAssert.AreEqual(
                    await File.ReadAllBytesAsync(Path.Combine(sealedPath, "snapshot.tar.age")).ConfigureAwait(false),
                    await File.ReadAllBytesAsync(Path.Combine(downloaded, "snapshot.tar.age")).ConfigureAwait(false));
                var offHostRecovered = Path.Combine(jobRoot, "off-host-recovered");
                var remoteUnseal = await RunArchiveAsync(
                    ["unseal", downloaded, identity, verifyKey, offHostRecovered]).ConfigureAwait(false);
                Assert.AreEqual(0, remoteUnseal.ExitCode, remoteUnseal.Output);
                var offHostSummary = await DistributedBackupRestorer.VerifyAsync(
                    offHostRecovered).ConfigureAwait(false);
                Assert.AreEqual(2L, offHostSummary.ReferenceCount);

                var offsiteRoot = Path.Combine(jobRoot, "offsite");
                Directory.CreateDirectory(offsiteRoot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var offsiteJob = await RunOffsiteBackupAsync(
                    jobConfigPath, offsiteRoot, recipients, signingKey, verifyKey,
                    connectionFile, archiveContainer.Name).ConfigureAwait(false);
                Assert.AreEqual(0, offsiteJob.ExitCode, offsiteJob.Output);
                var offsiteSealed = Directory.GetDirectories(offsiteRoot).Single();
                Assert.HasCount(2, Directory.GetFiles(offsiteSealed));
                Assert.IsTrue(await archiveContainer.GetBlobClient(
                    $"distributed-archives/v1/{Path.GetFileName(offsiteSealed)}/complete.json")
                    .ExistsAsync().ConfigureAwait(false));
                var offsiteDownloaded = Path.Combine(jobRoot, "offsite-downloaded");
                var offsiteFetch = await RunArchiveTransportCliAsync(
                    ["--fetch-distributed-archive", connectionFile,
                        archiveContainer.Name, Path.GetFileName(offsiteSealed),
                        offsiteDownloaded, verifyKey]).ConfigureAwait(false);
                Assert.AreEqual(0, offsiteFetch.ExitCode, offsiteFetch.Output);
                var offsiteRecovered = Path.Combine(jobRoot, "offsite-recovered");
                var offsiteUnseal = await RunArchiveAsync(
                    ["unseal", offsiteDownloaded, identity, verifyKey, offsiteRecovered]).ConfigureAwait(false);
                Assert.AreEqual(0, offsiteUnseal.ExitCode, offsiteUnseal.Output);
                var offsiteVerified = await DistributedBackupRestorer.VerifyAsync(
                    offsiteRecovered).ConfigureAwait(false);
                Assert.AreEqual(2L, offsiteVerified.ReferenceCount);

                var remoteCipher = archiveContainer.GetBlobClient(
                    $"distributed-archives/v1/{archiveId}/snapshot.tar.age");
                {
                    var tampered = new MemoryStream("tampered"u8.ToArray());
                    await using var tamperedLifetime = tampered.ConfigureAwait(false);
                    await remoteCipher.UploadAsync(tampered, overwrite: true).ConfigureAwait(false);
                }
                var rejected = Path.Combine(jobRoot, "tampered-download");
                var tamperedFetch = await RunArchiveTransportCliAsync(
                    ["--fetch-distributed-archive", connectionFile,
                        archiveContainer.Name, archiveId, rejected, verifyKey]).ConfigureAwait(false);
                Assert.AreNotEqual(0, tamperedFetch.ExitCode);
                Assert.IsFalse(Path.Exists(rejected));
            }
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
            await restoredContainer.DeleteIfExistsAsync().ConfigureAwait(false);
            await archiveContainer.DeleteIfExistsAsync().ConfigureAwait(false);
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task VersionTwoArchiveRestoresIntoGuardedDatabase()
    {
        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var targetDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var targetDatabaseLifetime = targetDatabase.ConfigureAwait(false);
        await PrepareAsync(sourceDatabase).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        await using var targetLifetime = target.ConfigureAwait(false);
        var objects = new InMemoryLargeObjectStore();
        var parent = Directory.CreateTempSubdirectory("mk8-version-two-restore-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, objects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable).ConfigureAwait(false);
            // Reproduce a genuine pre-receipt archive, not just an older format number.
            {
                var removeReceiptSchema = source.CreateCommand("DROP TABLE application_operation_receipts");
                await using var removeReceiptSchemaLifetime = removeReceiptSchema.ConfigureAwait(false);
                await removeReceiptSchema.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var dumpPath = Path.Combine(destination, "database.dump");
            await ReplaceLegacyDumpAsync(dumpPath, sourceDatabase.ConnectionString).ConfigureAwait(false);
            var metadataPath = Path.Combine(destination, "backup.json");
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false));
            Assert.IsNotNull(metadata);
            metadata["SchemaVersion"] = 2;
            metadata["DatabaseSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dumpPath).ConfigureAwait(false)));
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString()).ConfigureAwait(false);
            await RewriteChecksumsAsync(destination).ConfigureAwait(false);
            var verified = await RunVerifierCliAsync(destination).ConfigureAwait(false);
            Assert.AreEqual(0, verified.ExitCode, verified.Output);
            StringAssert.Contains(verified.Output, "snapshot v2: 0 references", StringComparison.Ordinal);

            var result = await DistributedBackupRestorer.RestoreAsync(
                destination, target, targetDatabase.ConnectionString,
                objects, PgRestoreExecutable).ConfigureAwait(false);
            Assert.AreEqual(0L, result.ReferenceCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(target).ConfigureAwait(false);
            var receiptCount = target.CreateCommand("SELECT count(*) FROM application_operation_receipts");
            await using var receiptCountLifetime = receiptCount.ConfigureAwait(false);
            Assert.AreEqual(0L, await receiptCount.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SnapshotExportKeepsBlobWhenItsRowIsDeletedDuringCopy scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SnapshotExportKeepsBlobWhenItsRowIsDeletedDuringCopy()
    {
        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var restoredDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var restoredDatabaseLifetime = restoredDatabase.ConfigureAwait(false);
        await PrepareAsync(sourceDatabase).ConfigureAwait(false);
        var exportSource = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var exportSourceLifetime = exportSource.ConfigureAwait(false);
        var applicationSource = NpgsqlDataSource.Create(
            sourceDatabase.ConnectionString);
        await using var applicationSourceLifetime = applicationSource.ConfigureAwait(false);
        var raw = new InMemoryLargeObjectStore();
        var content = "snapshot-visible content"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var upload = new MemoryStream(content, writable: false);
        await using var uploadLifetime = upload.ConfigureAwait(false);
        var written = await raw.PutIfAbsentAsync(
            "jmap/concurrent", upload, content.LongLength, sha256,
            "application/octet-stream").ConfigureAwait(false);
        var rowId = Guid.NewGuid();
        await InsertJmapBlobAsync(exportSource, rowId, written.Reference).ConfigureAwait(false);
        var blocking = new BlockingCopyStore(raw);
        var coordinated = new PostgresCoordinatedLargeObjectStore(applicationSource, raw);
        var parent = Directory.CreateTempSubdirectory("mk8-concurrent-export-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        using var exportCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<DistributedBackupExportResult>? exporting = null;
        Task<bool>? deletingBlob = null;
        try
        {
            // The finally block cancels/releases and joins both tasks before the configured source lifetimes end.
#pragma warning disable CA2025
            exporting = DistributedBackupExporter.ExportAsync(
                exportSource, blocking, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable, exportCancellation.Token);
#pragma warning restore CA2025

            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            {
                var connection = (await applicationSource.OpenConnectionAsync().ConfigureAwait(false));
                await using var connectionLifetime = connection.ConfigureAwait(false);
                var deleteRow = connection.CreateCommand();
                await using var deleteRowLifetime = deleteRow.ConfigureAwait(false);
                deleteRow.CommandText = "DELETE FROM jmap_blobs WHERE id = @id";
                deleteRow.Parameters.AddWithValue("id", rowId);
                Assert.AreEqual(1, await deleteRow.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            deletingBlob = coordinated.DeleteIfMatchAsync(written.Reference);
            await WaitForAdvisoryWaitAsync(applicationSource).ConfigureAwait(false);
            Assert.IsFalse(deletingBlob.IsCompleted);
            blocking.Release.TrySetResult(true);
            var result = await exporting.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Assert.AreEqual(1L, result.ReferenceCount);
            Assert.IsTrue(await deletingBlob.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            Assert.AreEqual(0, raw.ObjectCount);
            CollectionAssert.AreEqual(
                content, await File.ReadAllBytesAsync(
                    Path.Combine(destination, "blobs", sha256)).ConfigureAwait(false));

            await RestoreDumpAsync(
                Path.Combine(destination, "database.dump"),
                restoredDatabase.ConnectionString).ConfigureAwait(false);
            var restoredSource = NpgsqlDataSource.Create(
                restoredDatabase.ConnectionString);
            await using var restoredSourceLifetime = restoredSource.ConfigureAwait(false);
            var restored = (await restoredSource.OpenConnectionAsync().ConfigureAwait(false));
            await using var restoredLifetime = restored.ConfigureAwait(false);
            var query = restored.CreateCommand();
            await using var queryLifetime = query.ConfigureAwait(false);
            query.CommandText = "SELECT count(*) FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            Assert.AreEqual(1L, await query.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally
        {
            blocking.Release.TrySetResult(true);
            await exportCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                try { if (exporting is not null) await exporting.ConfigureAwait(false); }
                finally { if (deletingBlob is not null) await deletingBlob.ConfigureAwait(false); }
            }
            finally
            {
                parent.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MissingBlobFailsWithoutPublishingOrLeavingAnIncompleteExport()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        var objects = new InMemoryLargeObjectStore();
        var content = "missing from source store"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var upload = new MemoryStream(content, writable: false);
        await using var uploadLifetime = upload.ConfigureAwait(false);
        var written = await objects.PutIfAbsentAsync(
            "jmap/missing", upload, content.LongLength, sha256,
            "application/octet-stream").ConfigureAwait(false);
        await InsertJmapBlobAsync(dataSource, Guid.NewGuid(), written.Reference).ConfigureAwait(false);
        objects.Remove(written.Reference.ObjectName);
        var parent = Directory.CreateTempSubdirectory("mk8-failed-export-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupExporter.ExportAsync(
                    dataSource, objects, database.ConnectionString,
                    destination, PgDumpExecutable)).ConfigureAwait(false);
            Assert.IsFalse(Path.Exists(destination));
            Assert.HasCount(0, Directory.GetFileSystemEntries(parent.FullName));
            var lease = (await PostgresBlobDeletionBarrier
                .AcquireExclusiveAsync(dataSource).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            await using var leaseLifetime = lease.ConfigureAwait(false);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The RestoreRejectsCorruptArchiveAndNonemptyTargetBeforeBlobWrites scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task RestoreRejectsCorruptArchiveAndNonemptyTargetBeforeBlobWrites()
    {
        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var targetDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var targetDatabaseLifetime = targetDatabase.ConfigureAwait(false);
        await PrepareAsync(sourceDatabase).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        await using var targetLifetime = target.ConfigureAwait(false);
        var originalObjects = new InMemoryLargeObjectStore();
        var targetObjects = new InMemoryLargeObjectStore();
        var content = "verified restore input"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var upload = new MemoryStream(content, writable: false);
        await using var uploadLifetime = upload.ConfigureAwait(false);
        var written = await originalObjects.PutIfAbsentAsync(
            "jmap/restore-preflight", upload, content.LongLength, sha256,
            "application/octet-stream").ConfigureAwait(false);
        await InsertJmapBlobAsync(source, Guid.NewGuid(), written.Reference).ConfigureAwait(false);
        var parent = Directory.CreateTempSubdirectory("mk8-restore-preflight-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, originalObjects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable).ConfigureAwait(false);
            var blobFile = Path.Combine(destination, "blobs", sha256);
            await File.WriteAllBytesAsync(blobFile, "tampered"u8.ToArray()).ConfigureAwait(false);
            var invalidCli = await RunVerifierCliAsync(destination).ConfigureAwait(false);
            Assert.AreEqual(1, invalidCli.ExitCode, invalidCli.Output);
            StringAssert.Contains(invalidCli.Output, "checksum mismatch", StringComparison.Ordinal);
            var corrupt = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable)).ConfigureAwait(false);
            StringAssert.Contains(corrupt.Message, "checksum mismatch", StringComparison.Ordinal);
            Assert.AreEqual(0, targetObjects.ObjectCount);
            await DistributedRestoreActivationGuard.RequireReadyAsync(target).ConfigureAwait(false);
            {
                var marker = target.CreateCommand(
                             "SELECT to_regclass('public.mk8_restore_state') IS NULL");
                await using var markerLifetime = marker.ConfigureAwait(false);
                Assert.AreEqual(true, await marker.ExecuteScalarAsync().ConfigureAwait(false));
            }

            await File.WriteAllBytesAsync(blobFile, content).ConfigureAwait(false);
            {
                var connection = (await target.OpenConnectionAsync().ConfigureAwait(false));
                await using var connectionLifetime = connection.ConfigureAwait(false);
                var create = connection.CreateCommand();
                await using var createLifetime = create.ConfigureAwait(false);
                create.CommandText = "CREATE TABLE existing_target_data (id integer PRIMARY KEY)";
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var nonempty = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable)).ConfigureAwait(false);
            StringAssert.Contains(nonempty.Message, "not empty", StringComparison.Ordinal);
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
        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var targetDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var targetDatabaseLifetime = targetDatabase.ConfigureAwait(false);
        await PrepareAsync(sourceDatabase).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        await using var targetLifetime = target.ConfigureAwait(false);
        var originalObjects = new InMemoryLargeObjectStore();
        var targetObjects = new InMemoryLargeObjectStore();
        var content = "expected restore content"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var upload = new MemoryStream(content, writable: false);
        await using var uploadLifetime = upload.ConfigureAwait(false);
        var written = await originalObjects.PutIfAbsentAsync(
            "jmap/import-conflict", upload, content.LongLength, sha256,
            "application/octet-stream").ConfigureAwait(false);
        await InsertJmapBlobAsync(source, Guid.NewGuid(), written.Reference).ConfigureAwait(false);

        var conflicting = "different target content"u8.ToArray();
        var conflictingSha256 = Convert.ToHexStringLower(SHA256.HashData(conflicting));
        var conflictingUpload = new MemoryStream(conflicting, writable: false);
        await using var conflictingUploadLifetime = conflictingUpload.ConfigureAwait(false);
        await targetObjects.PutIfAbsentAsync(
            written.Reference.ObjectName, conflictingUpload, conflicting.LongLength,
            conflictingSha256, "application/octet-stream").ConfigureAwait(false);

        var parent = Directory.CreateTempSubdirectory("mk8-import-failed-restore-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, originalObjects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable).ConfigureAwait(false);
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable)).ConfigureAwait(false);
            StringAssert.Contains(failure.Message, "already uses that name", StringComparison.Ordinal);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedRestoreActivationGuard.RequireReadyAsync(target)).ConfigureAwait(false);
            var marker = target.CreateCommand(
                "SELECT state FROM public.mk8_restore_state");
            await using var markerLifetime = marker.ConfigureAwait(false);
            Assert.AreEqual("pending", await marker.ExecuteScalarAsync().ConfigureAwait(false));
            var tables = target.CreateCommand(
                "SELECT to_regclass('public.jmap_blobs') IS NULL");
            await using var tablesLifetime = tables.ConfigureAwait(false);
            Assert.AreEqual(true, await tables.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The RestoreRejectsManifestThatDoesNotMatchTheDumpBeforeRebinding scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task RestoreRejectsManifestThatDoesNotMatchTheDumpBeforeRebinding()
    {
        var sourceDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var sourceDatabaseLifetime = sourceDatabase.ConfigureAwait(false);
        var targetDatabase = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var targetDatabaseLifetime = targetDatabase.ConfigureAwait(false);
        await PrepareAsync(sourceDatabase).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(sourceDatabase.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var target = NpgsqlDataSource.Create(targetDatabase.ConnectionString);
        await using var targetLifetime = target.ConfigureAwait(false);
        var originalObjects = new InMemoryLargeObjectStore();
        var targetObjects = new InMemoryLargeObjectStore();
        var content = "manifest mismatch sentinel"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var upload = new MemoryStream(content, writable: false);
        await using var uploadLifetime = upload.ConfigureAwait(false);
        var written = await originalObjects.PutIfAbsentAsync(
            "jmap/mismatched", upload, content.LongLength, sha256,
            "application/octet-stream").ConfigureAwait(false);
        var rowId = Guid.NewGuid();
        await InsertJmapBlobAsync(source, rowId, written.Reference).ConfigureAwait(false);
        var parent = Directory.CreateTempSubdirectory("mk8-mismatch-restore-");
        var destination = Path.Combine(parent.FullName, "snapshot");
        try
        {
            await DistributedBackupExporter.ExportAsync(
                source, originalObjects, sourceDatabase.ConnectionString,
                destination, PgDumpExecutable).ConfigureAwait(false);
            var manifestPath = Path.Combine(destination, "references.jsonl");
            var original = JsonSerializer.Deserialize<DistributedBlobReferenceRow>(
                await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
            Assert.IsNotNull(original);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(original with { RowId = Guid.NewGuid() }) + "\n").ConfigureAwait(false);
            var metadataPath = Path.Combine(destination, "backup.json");
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false));
            Assert.IsNotNull(metadata);
            {
                var input = File.OpenRead(manifestPath);
                await using var inputLifetime = input.ConfigureAwait(false);
                metadata["ManifestSha256"] =
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(input).ConfigureAwait(false));
            }
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString()).ConfigureAwait(false);
            await RewriteChecksumsAsync(destination).ConfigureAwait(false);

            var mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupRestorer.RestoreAsync(
                    destination, target, targetDatabase.ConnectionString,
                    targetObjects, PgRestoreExecutable)).ConfigureAwait(false);
            StringAssert.Contains(mismatch.Message, "do not match", StringComparison.Ordinal);
            var blocked = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedRestoreActivationGuard.RequireReadyAsync(target)).ConfigureAwait(false);
            StringAssert.Contains(blocked.Message, "incomplete", StringComparison.Ordinal);
            var invalidSnapshot = Path.Combine(parent.FullName, "incomplete-source-snapshot");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                DistributedBackupExporter.ExportAsync(
                    target, targetObjects, targetDatabase.ConnectionString,
                    invalidSnapshot, PgDumpExecutable)).ConfigureAwait(false);
            Assert.IsFalse(Path.Exists(invalidSnapshot));
            var restored = (await target.OpenConnectionAsync().ConfigureAwait(false));
            await using var restoredLifetime = restored.ConfigureAwait(false);
            {
                var state = restored.CreateCommand();
                await using var stateLifetime = state.ConfigureAwait(false);
                state.CommandText = "SELECT state FROM public.mk8_restore_state WHERE id = 1";
                Assert.AreEqual("pending", await state.ExecuteScalarAsync().ConfigureAwait(false));
            }
            var query = restored.CreateCommand();
            await using var queryLifetime = query.ConfigureAwait(false);
            query.CommandText = "SELECT object_etag FROM jmap_blobs WHERE id = @id";
            query.Parameters.AddWithValue("id", rowId);
            Assert.AreEqual(written.Reference.EntityTag, await query.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    private static string PgDumpExecutable =>
        Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_PG_DUMP") ?? "pg_dump";

    private static async Task<(int ExitCode, string Output)> RunArchiveAsync(
        IReadOnlyList<string> arguments)
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
        var script = Path.Combine(repository, "deploy", "scripts", "mk8-distributed-archive");
        Assert.IsTrue(File.Exists(assembly), "The management CLI executable is missing.");
        Assert.IsTrue(File.Exists(script), "The distributed archive wrapper is missing.");
        return await RunExternalAsync(script, [.. arguments, host, assembly]).ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Output)> RunArchiveTransportCliAsync(
        IReadOnlyList<string> arguments)
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
        return await RunExternalAsync(host, [assembly, .. arguments]).ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Output)> RunOffsiteBackupAsync(
        string config, string root, string recipients, string signingKey,
        string verifyKey, string connectionFile, string container)
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
        var script = Path.Combine(
            repository, "deploy", "scripts", "mk8-distributed-offsite-backup");
        var archiveTool = Path.Combine(
            repository, "deploy", "scripts", "mk8-distributed-archive");
        Assert.IsTrue(File.Exists(assembly), "The management CLI executable is missing.");
        Assert.IsTrue(File.Exists(script), "The off-site backup wrapper is missing.");
        return await RunExternalAsync(script,
            [config, root, recipients, signingKey, verifyKey, connectionFile,
                container, host, assembly, archiveTool], PgDumpExecutable).ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Output)> RunExternalAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? pgDumpExecutable = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (pgDumpExecutable is not null)
            start.Environment["MK8EMAIL_PG_DUMP_EXECUTABLE"] = pgDumpExecutable;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The archive tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // Archive sealing and extraction sync durable files; a loaded CI disk can
        // exceed the short subprocess budget without a hung archive process.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail("The archive tool exceeded its deadline.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }

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
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail("The distributed backup job exceeded its deadline.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
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
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail("The archive verification CLI exceeded its deadline.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
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
        var connection = (await source.OpenConnectionAsync().ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var insert = connection.CreateCommand();
        await using var insertLifetime = insert.ConfigureAwait(false);
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
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task InsertGatewayTrafficAsync(
        NpgsqlDataSource source,
        Guid rowId,
        LargeObjectReference reference)
    {
        var connection = (await source.OpenConnectionAsync().ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var insert = connection.CreateCommand();
        await using var insertLifetime = insert.ConfigureAwait(false);
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
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task PrepareAsync(PostgresTestDatabase database)
    {
        var context = new EmailDbContext(
            new DbContextOptionsBuilder<EmailDbContext>()
                .UseNpgsql(database.ConnectionString).Options);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(source).ConfigureAwait(false);
    }

    private static async Task VerifyChecksumsAsync(string destination)
    {
        foreach (var line in await File.ReadAllLinesAsync(
                     Path.Combine(destination, "SHA256SUMS")).ConfigureAwait(false))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            Assert.IsTrue(separator > 0);
            var path = Path.Combine(destination, line[(separator + 2)..]);
            var input = File.OpenRead(path);
            await using var inputLifetime = input.ConfigureAwait(false);
            Assert.AreEqual(
                line[..separator],
                Convert.ToHexStringLower(await SHA256.HashDataAsync(input).ConfigureAwait(false)), StringComparer.Ordinal);
        }
    }

    private static async Task RewriteChecksumsAsync(string destination)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     destination, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "SHA256SUMS", StringComparison.Ordinal))
                continue;
            var input = File.OpenRead(file);
            await using var inputLifetime = input.ConfigureAwait(false);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input).ConfigureAwait(false));
            lines.Add($"{sha256}  {Path.GetRelativePath(destination, file)}");
        }
        await File.WriteAllLinesAsync(Path.Combine(destination, "SHA256SUMS"), lines).ConfigureAwait(false);
    }

    private static async Task WaitForAdvisoryWaitAsync(NpgsqlDataSource source)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var connection = (await source.OpenConnectionAsync().ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database()
                    AND wait_event_type = 'Lock'
                    AND wait_event = 'advisory'
                """;
            if ((long)(await command.ExecuteScalarAsync().ConfigureAwait(false))! > 0)
                return;
            await Task.Delay(20).ConfigureAwait(false);
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
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            Assert.Fail($"pg_restore failed: {await error.ConfigureAwait(false)}");
    }

    private static async Task ReplaceLegacyDumpAsync(string dump, string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo(PgDumpExecutable) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "--format=custom", "--no-owner", "--no-acl", "--no-password",
                     $"--host={database.Host}", $"--port={database.Port}", $"--username={database.Username}",
                     $"--dbname={database.Database}", $"--file={dump}" })
            start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = database.Password;
        using var process = Process.Start(start) ?? throw new AssertFailedException("pg_dump did not start.");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, await error.ConfigureAwait(false));
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
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
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await inner.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> DeleteIfMatchAsync(
            LargeObjectReference reference,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfMatchAsync(reference, cancellationToken);
    }
}
