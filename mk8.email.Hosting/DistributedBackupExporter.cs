using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Hosting;

public sealed record DistributedBackupExportResult(
    long ReferenceCount,
    long UniqueContentCount,
    string DatabaseSha256,
    string ManifestSha256);

/// <summary>
/// Exports one PostgreSQL snapshot and its referenced Azure Blob-compatible bytes.
/// This produces a backup candidate; restore, ETag rebind, and off-host retention
/// are separate gates before the artifact is a verified disaster-recovery backup.
/// </summary>
public static class DistributedBackupExporter
{
    public static async Task<DistributedBackupExportResult> ExportAsync(
        NpgsqlDataSource dataSource,
        ILargeObjectStore objects,
        string connectionString,
        string destinationDirectory,
        string pgDumpExecutable = "pg_dump",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(objects);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed snapshot export requires Linux.");
        if (objects.Provider != LargeObjectProviders.AzureBlob)
            throw new InvalidOperationException("Distributed backup requires Azure Blob protocol.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        if (string.IsNullOrWhiteSpace(pgDumpExecutable))
            throw new ArgumentException("A pg_dump executable is required.", nameof(pgDumpExecutable));
        if (!Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("An absolute backup destination is required.", nameof(destinationDirectory));

        var destination = Path.GetFullPath(destinationDirectory);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The backup destination has no parent.", nameof(destinationDirectory));
        if (!Directory.Exists(parent)
            || File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)
            || (File.GetUnixFileMode(parent)
                & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException(
                "The backup parent must be an existing, private, non-symlink directory.");
        }
        if (Path.Exists(destination))
            throw new IOException("The backup destination already exists.");

        var stage = Path.Combine(parent, $".incomplete-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(stage, UnixFileMode.UserRead
            | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var blobDirectory = Path.Combine(stage, "blobs");
            Directory.CreateDirectory(blobDirectory, UnixFileMode.UserRead
                | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var lease = await PostgresBlobDeletionBarrier.AcquireExclusiveAsync(
                dataSource, cancellationToken);
            await using var transaction = await lease.Connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            await using (var readOnly = lease.Connection.CreateCommand())
            {
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            string snapshot;
            await using (var export = lease.Connection.CreateCommand())
            {
                export.Transaction = transaction;
                export.CommandText = "SELECT pg_export_snapshot()";
                snapshot = (string)(await export.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("PostgreSQL did not export a snapshot."));
            }
            await DistributedBlobReferenceInventory.ValidateSchemaAsync(
                lease.Connection, transaction, cancellationToken);

            var dumpPath = Path.Combine(stage, "database.dump");
            await RunPgDumpAsync(
                connectionString, snapshot, dumpPath, pgDumpExecutable, cancellationToken);
            await using (var dumpFile = new FileStream(
                             dumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                dumpFile.Flush(flushToDisk: true);
            }

            long references = 0;
            var manifestPath = Path.Combine(stage, "references.jsonl");
            await using (var manifestFile = new FileStream(
                             manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            await using (var manifest = new StreamWriter(
                             manifestFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                             leaveOpen: true))
            {
                await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                                   lease.Connection, transaction, cancellationToken))
                {
                    var target = Path.Combine(blobDirectory, row.Reference.Sha256);
                    await CopyAndVerifyAsync(objects, row.Reference, target, cancellationToken);
                    references++;
                    await manifest.WriteLineAsync(
                        JsonSerializer.Serialize(row).AsMemory(), cancellationToken);
                }
                await manifest.FlushAsync(cancellationToken);
                manifestFile.Flush(flushToDisk: true);
            }

            var uniqueContent = Directory.EnumerateFiles(blobDirectory).LongCount();
            await transaction.CommitAsync(cancellationToken);

            var dumpSha256 = await HashFileAsync(dumpPath, cancellationToken);
            var manifestSha256 = await HashFileAsync(manifestPath, cancellationToken);
            var metadataPath = Path.Combine(stage, "backup.json");
            await File.WriteAllTextAsync(
                metadataPath,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    ExportedAtUtc = DateTimeOffset.UtcNow,
                    Snapshot = snapshot,
                    ReferenceCount = references,
                    UniqueContentCount = uniqueContent,
                    DatabaseSha256 = dumpSha256,
                    ManifestSha256 = manifestSha256,
                }),
                cancellationToken);
            await using (var metadataFile = new FileStream(
                             metadataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                metadataFile.Flush(flushToDisk: true);
            }

            var checksumPath = Path.Combine(stage, "SHA256SUMS");
            await using (var checksumFile = new FileStream(
                             checksumPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            await using (var checksums = new StreamWriter(checksumFile))
            {
                foreach (var file in Directory.EnumerateFiles(
                             stage, "*", SearchOption.AllDirectories))
                {
                    if (file == checksumPath)
                        continue;
                    var relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
                    await checksums.WriteLineAsync(
                        $"{await HashFileAsync(file, cancellationToken)}  {relative}".AsMemory(),
                        cancellationToken);
                }
                await checksums.FlushAsync(cancellationToken);
                checksumFile.Flush(flushToDisk: true);
            }
            Directory.Move(stage, destination);
            return new DistributedBackupExportResult(
                references, uniqueContent, dumpSha256, manifestSha256);
        }
        catch
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static async Task CopyAndVerifyAsync(
        ILargeObjectStore objects,
        LargeObjectReference reference,
        string target,
        CancellationToken cancellationToken)
    {
        var partial = target + $".partial-{Guid.CreateVersion7():N}";
        try
        {
            await using var file = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            using var hash = SHA256.Create();
            await using (var digest = new CryptoStream(
                             file, hash, CryptoStreamMode.Write, leaveOpen: true))
            {
                await objects.CopyToAsync(reference, digest, cancellationToken);
                digest.FlushFinalBlock();
            }
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
            if (file.Length != reference.Length
                || !CryptographicOperations.FixedTimeEquals(
                    hash.Hash!, Convert.FromHexString(reference.Sha256)))
            {
                throw new InvalidOperationException(
                    $"The exported Blob {reference.ObjectName} failed length or SHA-256 verification.");
            }

            if (!File.Exists(target))
                File.Move(partial, target);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static async Task RunPgDumpAsync(
        string connectionString,
        string snapshot,
        string dumpPath,
        string executable,
        CancellationToken cancellationToken)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--format=custom");
        start.ArgumentList.Add("--no-owner");
        start.ArgumentList.Add("--no-acl");
        start.ArgumentList.Add("--no-password");
        start.ArgumentList.Add($"--snapshot={snapshot}");
        start.ArgumentList.Add($"--file={dumpPath}");
        start.ArgumentList.Add($"--host={database.Host}");
        start.ArgumentList.Add($"--port={database.Port}");
        start.ArgumentList.Add($"--username={database.Username}");
        start.ArgumentList.Add(database.Database
            ?? throw new InvalidOperationException("The PostgreSQL database name is missing."));
        start.Environment["PGPASSWORD"] = database.Password;
        start.Environment["PGCONNECT_TIMEOUT"] = "15";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("pg_dump could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"pg_dump exited with {process.ExitCode}: {(await error).Trim()}");
            }
            _ = await output;
            _ = await error;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }
}
