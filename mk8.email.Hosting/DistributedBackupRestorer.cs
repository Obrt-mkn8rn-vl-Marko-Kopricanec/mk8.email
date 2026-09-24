using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mk8.email.Contracts.Storage;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Hosting;

/// <summary>
/// Restores a verified distributed export into an empty PostgreSQL database and
/// an Azure Blob-compatible store, then atomically rebinds every database ETag.
/// Target services must remain stopped until this method succeeds.
/// </summary>
public static class DistributedBackupRestorer
{
    // Instantiated by System.Text.Json when verifying the sealed backup metadata.
#pragma warning disable CA1812
    private sealed record BackupMetadata(
        int SchemaVersion,
        long ReferenceCount,
        long UniqueContentCount,
        string DatabaseSha256,
        string ManifestSha256);
#pragma warning restore CA1812

    private sealed record RebindingRow(
        DistributedBlobReferenceRow Original,
        string NewEntityTag);

    public static async Task<DistributedBackupArchiveSummary> VerifyAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        var metadata = await VerifyArchiveAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        return new DistributedBackupArchiveSummary(
            metadata.SchemaVersion,
            metadata.ReferenceCount,
            metadata.UniqueContentCount,
            metadata.DatabaseSha256,
            metadata.ManifestSha256);
    }

    public static async Task<DistributedBackupRestoreResult> RestoreAsync(
        string backupDirectory,
        NpgsqlDataSource targetDataSource,
        string targetConnectionString,
        ILargeObjectStore targetObjects,
        string pgRestoreExecutable = "pg_restore",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetDataSource);
        ArgumentNullException.ThrowIfNull(targetObjects);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed snapshot restore requires Linux.");
        if (!string.Equals(targetObjects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Distributed restore requires Azure Blob protocol.");
        if (string.IsNullOrWhiteSpace(targetConnectionString))
            throw new ArgumentException("A target PostgreSQL connection is required.",
                nameof(targetConnectionString));
        if (string.IsNullOrWhiteSpace(pgRestoreExecutable))
            throw new ArgumentException("A pg_restore executable is required.",
                nameof(pgRestoreExecutable));

        var backup = await VerifyArchiveAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        await DistributedRestoreActivationGuard.BeginRestoreAsync(
            targetDataSource, backup.DatabaseSha256, backup.ManifestSha256,
            cancellationToken).ConfigureAwait(false);
        var temporary = Directory.CreateTempSubdirectory("mk8-distributed-restore-");
        try
        {
            var rebindingsPath = Path.Combine(temporary.FullName, "rebindings.jsonl");
            var imported = await ImportObjectsAsync(
                backupDirectory, backup, targetObjects, rebindingsPath, cancellationToken).ConfigureAwait(false);
            await RunPgRestoreAsync(
                targetConnectionString,
                Path.Combine(backupDirectory, "database.dump"),
                pgRestoreExecutable,
                cancellationToken).ConfigureAwait(false);
            await RebindDatabaseAsync(
                targetDataSource, rebindingsPath, backup.ReferenceCount,
                cancellationToken).ConfigureAwait(false);
            return new DistributedBackupRestoreResult(backup.ReferenceCount, imported);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    // Keep the complete archive integrity gate together before restore is admitted.
#pragma warning disable MA0051
    private static async Task<BackupMetadata> VerifyArchiveAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(backupDirectory))
#pragma warning restore MA0051
            throw new ArgumentException("An absolute backup directory is required.",
                nameof(backupDirectory));
        var root = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(root)
            || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The backup directory is missing or unsafe.");
        }
        var blobDirectory = Path.Combine(root, "blobs");
        if (!Directory.Exists(blobDirectory)
            || File.GetAttributes(blobDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The backup Blob directory is missing or unsafe.");
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!string.Equals(directory, blobDirectory, StringComparison.Ordinal)
                || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The backup contains an unexpected directory.");
            }
        }
        if (Directory.EnumerateDirectories(blobDirectory).Any())
            throw new InvalidOperationException("The backup Blob directory is nested.");

        var remaining = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("The backup contains a symbolic link.");
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative is not ("database.dump" or "references.jsonl" or "backup.json"
                    or "SHA256SUMS")
                && !(relative.StartsWith("blobs/", StringComparison.Ordinal)
                    && IsSha256(relative[6..])))
            {
                throw new InvalidOperationException("The backup contains an unexpected file.");
            }
            if (!string.Equals(relative, "SHA256SUMS", StringComparison.Ordinal))
                remaining.Add(relative);
        }
        if (!File.Exists(Path.Combine(root, "SHA256SUMS"))
            || !remaining.Contains("database.dump")
            || !remaining.Contains("references.jsonl")
            || !remaining.Contains("backup.json"))
        {
            throw new InvalidOperationException("The backup is incomplete.");
        }

        var checksums = File.OpenRead(Path.Combine(root, "SHA256SUMS"));
        await using (checksums.ConfigureAwait(false))
        using (var reader = new StreamReader(checksums))
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var separator = line.IndexOf("  ", StringComparison.Ordinal);
                if (separator != 64 || !IsSha256(line[..separator]))
                    throw new InvalidOperationException("The backup checksum list is invalid.");
                var relative = line[(separator + 2)..];
                if (!remaining.Remove(relative))
                    throw new InvalidOperationException("The backup checksum list has an unknown or duplicate file.");
                var actual = await HashFileAsync(Path.Combine(root, relative), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, line[..separator], StringComparison.Ordinal))
                    throw new InvalidOperationException($"Backup checksum mismatch: {relative}.");
            }
        }
        if (remaining.Count != 0)
            throw new InvalidOperationException("The backup checksum list omits a file.");

        BackupMetadata metadata;
        var input = File.OpenRead(Path.Combine(root, "backup.json"));
        await using (input.ConfigureAwait(false))
        {
            metadata = await JsonSerializer.DeserializeAsync<BackupMetadata>(
                    input, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The backup metadata is missing.");
        }
        var databaseSha256 = await HashFileAsync(
            Path.Combine(root, "database.dump"), cancellationToken).ConfigureAwait(false);
        var manifestSha256 = await HashFileAsync(
            Path.Combine(root, "references.jsonl"), cancellationToken).ConfigureAwait(false);
        if (metadata.SchemaVersion is not (2 or 3)
            || metadata.ReferenceCount < 0
            || metadata.UniqueContentCount < 0
            || !string.Equals(metadata.DatabaseSha256, databaseSha256, StringComparison.Ordinal)
            || !string.Equals(metadata.ManifestSha256, manifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backup metadata does not match its files.");
        }

        var rows = new HashSet<(string Source, Guid RowId)>();
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        long count = 0;
        await foreach (var row in ReadManifestAsync(root, cancellationToken).ConfigureAwait(false))
        {
            ValidateManifestRow(row, root);
            if (!rows.Add((row.Source, row.RowId)))
                throw new InvalidOperationException("The backup manifest duplicates a database reference.");
            blobs.Add(row.Reference.Sha256);
            count++;
        }
        if (count != metadata.ReferenceCount
            || blobs.Count != metadata.UniqueContentCount
            || Directory.EnumerateFiles(blobDirectory).LongCount() != metadata.UniqueContentCount)
        {
            throw new InvalidOperationException("The backup manifest counts do not match its files.");
        }
        return metadata;
    }

    // Keep duplicate-name validation and ETag manifest construction in one pass.
#pragma warning disable MA0051
    private static async Task<long> ImportObjectsAsync(
        string backupDirectory,
        BackupMetadata metadata,
        ILargeObjectStore targetObjects,
        string rebindingsPath,
        CancellationToken cancellationToken)
    {
        var imported = new Dictionary<string, (LargeObjectReference Reference, string ContentType)>(
            StringComparer.Ordinal);
#pragma warning restore MA0051
        var rebindingsFile = new FileStream(
            rebindingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await using (rebindingsFile.ConfigureAwait(false))
        {
            var rebindings = new StreamWriter(
                rebindingsFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
            await using var rebindingsLifetime = rebindings.ConfigureAwait(false);
            long count = 0;
            await foreach (var row in ReadManifestAsync(backupDirectory, cancellationToken).ConfigureAwait(false))
            {
                if (!imported.TryGetValue(row.Reference.ObjectName, out var cached))
                {
                    // The configured async lifetime below owns this stream on every exit path.
#pragma warning disable CA2000
                    var source = File.OpenRead(
                        Path.Combine(backupDirectory, "blobs", row.Reference.Sha256));
#pragma warning restore CA2000
                    await using var sourceLifetime = source.ConfigureAwait(false);
                    var written = await targetObjects.PutIfAbsentAsync(
                        row.Reference.ObjectName,
                        source,
                        row.Reference.Length,
                        row.Reference.Sha256,
                        row.ContentType,
                        cancellationToken).ConfigureAwait(false);
                    cached = (written.Reference, row.ContentType);
                    if (!string.Equals(cached.Reference.Provider, row.Reference.Provider, StringComparison.Ordinal)
                        || !string.Equals(cached.Reference.ObjectName, row.Reference.ObjectName, StringComparison.Ordinal)
                        || cached.Reference.Length != row.Reference.Length
                        || !string.Equals(cached.Reference.Sha256, row.Reference.Sha256, StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(cached.Reference.EntityTag))
                    {
                        throw new InvalidOperationException("The target Blob reference differs from the export.");
                    }
                    await VerifyImportedObjectAsync(targetObjects, cached.Reference, cancellationToken).ConfigureAwait(false);
                    imported.Add(row.Reference.ObjectName, cached);
                }
                else if (cached.Reference.Length != row.Reference.Length
                    || !string.Equals(cached.Reference.Sha256, row.Reference.Sha256, StringComparison.Ordinal)
                    || !string.Equals(cached.ContentType, row.ContentType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The backup uses one Blob name for inconsistent content.");
                }

                await rebindings.WriteLineAsync(
                    JsonSerializer.Serialize(new RebindingRow(row, cached.Reference.EntityTag)).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                count++;
            }
            if (count != metadata.ReferenceCount)
                throw new InvalidOperationException("The backup manifest changed during restore.");
            await rebindings.FlushAsync(cancellationToken).ConfigureAwait(false);
            // Persist ETag rebindings before applying them to the target database.
#pragma warning disable CA1849
            rebindingsFile.Flush(flushToDisk: true);
#pragma warning restore CA1849
            return imported.Count;
        }
    }

    private static async Task VerifyImportedObjectAsync(
        ILargeObjectStore objects,
        LargeObjectReference reference,
        CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        var sink = new CryptoStream(
                         Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
        await using (sink.ConfigureAwait(false))
        {
            await objects.CopyToAsync(reference, sink, cancellationToken).ConfigureAwait(false);
            await sink.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!CryptographicOperations.FixedTimeEquals(
                hash.Hash!, Convert.FromHexString(reference.Sha256)))
        {
            throw new InvalidOperationException("The target Blob content failed SHA-256 verification.");
        }
    }

    // One serializable transaction must validate and rebind all Blob references.
#pragma warning disable MA0051
    private static async Task RebindDatabaseAsync(
        NpgsqlDataSource targetDataSource,
        string rebindingsPath,
        long expectedCount,
        CancellationToken cancellationToken)
    {
        var connection = await targetDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0051
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            await DistributedBlobReferenceInventory.ValidateSchemaAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var create = connection.CreateCommand();
            await using (create.ConfigureAwait(false))
            {
                create.Transaction = transaction;
                create.CommandText = """
                CREATE TEMP TABLE restore_blob_rebindings (
                    source text NOT NULL,
                    row_id uuid NOT NULL,
                    provider text NOT NULL,
                    object_name text NOT NULL,
                    length bigint NOT NULL,
                    sha256 text NOT NULL,
                    old_etag text NOT NULL,
                    content_type text NOT NULL,
                    new_etag text NOT NULL,
                    PRIMARY KEY (source, row_id)
                ) ON COMMIT DROP
                """;
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var importer = await connection.BeginBinaryImportAsync(
                             "COPY restore_blob_rebindings "
                             + "(source, row_id, provider, object_name, length, sha256, "
                             + "old_etag, content_type, new_etag) FROM STDIN (FORMAT BINARY)",
                             cancellationToken).ConfigureAwait(false);
            await using (importer.ConfigureAwait(false))
            {
                await foreach (var row in ReadRebindingsAsync(rebindingsPath, cancellationToken).ConfigureAwait(false))
                {
                    await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                    importer.Write(row.Original.Source, NpgsqlDbType.Text);
                    importer.Write(row.Original.RowId, NpgsqlDbType.Uuid);
                    importer.Write(row.Original.Reference.Provider, NpgsqlDbType.Text);
                    importer.Write(row.Original.Reference.ObjectName, NpgsqlDbType.Text);
                    importer.Write(row.Original.Reference.Length, NpgsqlDbType.Bigint);
                    importer.Write(row.Original.Reference.Sha256, NpgsqlDbType.Text);
                    importer.Write(row.Original.Reference.EntityTag, NpgsqlDbType.Text);
                    importer.Write(row.Original.ContentType, NpgsqlDbType.Text);
                    importer.Write(row.NewEntityTag, NpgsqlDbType.Text);
                }
                await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            var importedCount = await CountAsync(connection, transaction,
                cancellationToken).ConfigureAwait(false);
            if (importedCount != expectedCount
                || await CountDifferencesAsync(
                    connection, transaction, "old_etag", cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidOperationException(
                    "The restored database references do not match the backup manifest.");
            }

            long updated = 0;
            foreach (var source in DistributedBlobReferenceInventory.Sources)
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.Transaction = transaction;
                    // Every interpolated identifier comes from the fixed internal Sources list.
#pragma warning disable CA2100
                    command.CommandText = $"""
                UPDATE {source.Table} AS target
                SET {source.EntityTag} = binding.new_etag
                FROM restore_blob_rebindings AS binding
                WHERE binding.source = @source
                    AND target.id = binding.row_id
                    AND target.{source.Name} = binding.object_name
                    AND target.{source.EntityTag} = binding.old_etag
                """;
#pragma warning restore CA2100
                    command.Parameters.AddWithValue("source", source.Key);
                    updated += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (updated != expectedCount
                || await CountDifferencesAsync(
                    connection, transaction, "new_etag", cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidOperationException("The restored Blob ETags did not rebind completely.");
            }
            await DistributedRestoreActivationGuard.CompleteRestoreAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> CountDifferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string expectedEtagColumn,
        CancellationToken cancellationToken)
    {
        if (expectedEtagColumn is not ("old_etag" or "new_etag"))
            throw new ArgumentOutOfRangeException(nameof(expectedEtagColumn));
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            // The CTE comes from fixed schema identifiers; expectedEtagColumn is allowlisted above.
#pragma warning disable CA2100
            command.CommandText = $"""
            WITH db_refs (source, row_id, provider, object_name, length,
                          sha256, etag, content_type) AS (
                {DistributedBlobReferenceInventory.BuildQuerySql()}
            )
            SELECT count(*)
            FROM db_refs AS db
            FULL OUTER JOIN restore_blob_rebindings AS binding
                ON db.source = binding.source AND db.row_id = binding.row_id
            WHERE db.source IS NULL OR binding.source IS NULL
                OR db.provider IS DISTINCT FROM binding.provider
                OR db.object_name IS DISTINCT FROM binding.object_name
                OR db.length IS DISTINCT FROM binding.length
                OR db.sha256 IS DISTINCT FROM binding.sha256
                OR db.etag IS DISTINCT FROM binding.{expectedEtagColumn}
                OR db.content_type IS DISTINCT FROM binding.content_type
            """;
#pragma warning restore CA2100
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM restore_blob_rebindings";
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    private static async Task RunPgRestoreAsync(
        string connectionString,
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
        start.ArgumentList.Add("--single-transaction");
        start.ArgumentList.Add("--exit-on-error");
        start.ArgumentList.Add("--no-owner");
        start.ArgumentList.Add("--no-acl");
        start.ArgumentList.Add("--no-password");
        start.ArgumentList.Add($"--host={database.Host}");
        start.ArgumentList.Add($"--port={database.Port}");
        start.ArgumentList.Add($"--username={database.Username}");
        start.ArgumentList.Add($"--dbname={database.Database}");
        start.ArgumentList.Add(dumpPath);
        start.Environment["PGPASSWORD"] = database.Password;
        start.Environment["PGCONNECT_TIMEOUT"] = "15";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("pg_restore could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"pg_restore exited with {process.ExitCode}: {(await error.ConfigureAwait(false)).Trim()}");
            _ = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async IAsyncEnumerable<DistributedBlobReferenceRow> ReadManifestAsync(
        string backupDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = File.OpenRead(Path.Combine(backupDirectory, "references.jsonl"));
        await using (input.ConfigureAwait(false))
        {
            using var reader = new StreamReader(input);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return JsonSerializer.Deserialize<DistributedBlobReferenceRow>(line)
                    ?? throw new InvalidOperationException("The backup manifest contains a null row.");
            }
        }
    }

    private static async IAsyncEnumerable<RebindingRow> ReadRebindingsAsync(
        string rebindingsPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = File.OpenRead(rebindingsPath);
        await using (input.ConfigureAwait(false))
        {
            using var reader = new StreamReader(input);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return JsonSerializer.Deserialize<RebindingRow>(line)
                    ?? throw new InvalidOperationException("A restore rebinding row is missing.");
            }
        }
    }

    private static void ValidateManifestRow(
        DistributedBlobReferenceRow row,
        string backupDirectory)
    {
        if (row is null || row.Reference is null
            || row.RowId == Guid.Empty
            || !DistributedBlobReferenceInventory.Sources.Any(source => string.Equals(source.Key, row.Source, StringComparison.Ordinal))
            || !string.Equals(row.Reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(row.Reference.ObjectName)
            || row.Reference.ObjectName.Length > 1024
            || row.Reference.Length < 0
            || !IsSha256(row.Reference.Sha256)
            || string.IsNullOrWhiteSpace(row.Reference.EntityTag)
            || string.IsNullOrWhiteSpace(row.ContentType)
            || row.ContentType.Length > 255
            || row.ContentType.Any(char.IsControl))
        {
            throw new InvalidOperationException("The backup manifest contains an invalid reference.");
        }
        var contentPath = Path.Combine(backupDirectory, "blobs", row.Reference.Sha256);
        if (!File.Exists(contentPath)
            || new FileInfo(contentPath).Length != row.Reference.Length)
        {
            throw new InvalidOperationException("The backup manifest references missing Blob content.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var input = File.OpenRead(path);
        await using (input.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        }
    }
}
