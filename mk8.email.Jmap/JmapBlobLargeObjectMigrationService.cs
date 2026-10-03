using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

public sealed class JmapBlobLargeObjectMigrationService(
    EmailDbContext database,
    ILargeObjectStore objects,
    ILogger<JmapBlobLargeObjectMigrationService> logger)
{
    private static readonly Action<ILogger, Guid, Exception?> RollbackWarning = LoggerMessage.Define<Guid>(
        LogLevel.Warning, new EventId(1213, "BlobMigrationRollback"),
        "Could not roll back failed JMAP blob migration for {BlobId}");
    private static readonly Action<ILogger, string, Exception?> CleanupWarning = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1214, "BlobMigrationCleanup"),
        "Could not clean up JMAP migration object {ObjectName}");
    private const int BatchSize = 50;
    private const long MigrationLockKey = 5_568_199_231_845_892_162;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "JMAP blob migration requires an Azure Blob-compatible object store.");
        }
        if (database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "JMAP blob migration must own its database transactions.");
        }

        if (!IsPostgreSql())
        {
            await MigrateRowsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var connection = database.Database.GetDbConnection();
        var closeConnection = connection.State == ConnectionState.Closed;
        var lockTaken = false;
        try
        {
            if (closeConnection)
                await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_lock({MigrationLockKey})",
                cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            await MigrateRowsAsync(cancellationToken).ConfigureAwait(false);
            await EnforceExternalStorageAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (lockTaken)
                {
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_unlock({MigrationLockKey})",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (closeConnection)
                    await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task MigrateRowsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var legacy = await database.JmapBlobs
                .Where(blob => blob.Content != null)
                .OrderBy(blob => blob.CreatedAt)
                .ThenBy(blob => blob.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.Count == 0)
                break;

            for (var blobIndex = 0; blobIndex < legacy.Count; blobIndex++)
                await MigrateAsync(legacy[blobIndex], cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
        }
    }

    private async Task EnforceExternalStorageAsync(CancellationToken cancellationToken)
    {
        var ownsTransaction = database.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            await database.Database.ExecuteSqlRawAsync(
                """
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conrelid = 'jmap_blobs'::regclass
                          AND conname = 'ck_jmap_blobs_external_storage'
                    ) THEN
                        ALTER TABLE jmap_blobs
                            ADD CONSTRAINT ck_jmap_blobs_external_storage CHECK (
                                content IS NULL
                                AND object_provider = 'azure-blob'
                                AND object_name IS NOT NULL
                                AND object_sha256 IS NOT NULL
                                AND object_etag IS NOT NULL) NOT VALID;
                    END IF;
                END
                $migration$;
                ALTER TABLE jmap_blobs
                    VALIDATE CONSTRAINT ck_jmap_blobs_external_storage;
                """,
                cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task MigrateAsync(
        JmapBlobDB blob,
        CancellationToken cancellationToken)
    {
        var content = blob.Content
            ?? throw new InvalidOperationException("The legacy JMAP blob content is missing.");
        if (blob.SizeBytes != content.LongLength)
        {
            throw new InvalidOperationException(
                $"Legacy JMAP blob {blob.Id:D} has inconsistent length metadata.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var objectName = BuildObjectName(blob.AccountId, blob.Id);
        var ownsTransaction = database.Database.IsRelational();
        var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        LargeObjectWriteResult? written = null;
        var commitAttempted = false;
        try
        {
            var source = new MemoryStream(content, writable: false);
            await using var sourceLifetime = source.ConfigureAwait(false);
            written = await objects.PutIfAbsentAsync(
                objectName,
                source,
                content.LongLength,
                hash,
                blob.ContentType,
                cancellationToken).ConfigureAwait(false);
            ApplyReference(blob, written.Reference);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await RollbackBestEffortAsync(transaction, blob.Id).ConfigureAwait(false);
            if (written is { Created: true } && !commitAttempted)
                await CleanupBestEffortAsync(written.Reference).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Migration compensation must preserve the original failure even if the database connection also fails.")]
    private async Task RollbackBestEffortAsync(IDbContextTransaction? transaction, Guid blobId)
    {
        if (transaction is null) return;
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { RollbackWarning(logger, blobId, exception); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Conditional cleanup of an uncommitted Azure object must not mask the migration failure.")]
    private async Task CleanupBestEffortAsync(LargeObjectReference reference)
    {
        try { await objects.DeleteIfMatchAsync(reference, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { CleanupWarning(logger, reference.ObjectName, exception); }
    }

    internal static string BuildObjectName(Guid accountId, Guid blobId) =>
        $"jmap/uploads/{accountId:N}/{blobId:N}";

    private bool IsPostgreSql() => string.Equals(
        database.Database.ProviderName,
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        StringComparison.Ordinal);

    internal static void ApplyReference(JmapBlobDB blob, LargeObjectReference reference)
    {
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("JMAP blobs require the Azure Blob storage provider.");
        blob.Content = null;
        blob.ObjectProvider = reference.Provider;
        blob.ObjectName = reference.ObjectName;
        blob.ObjectSha256 = reference.Sha256;
        blob.ObjectEntityTag = reference.EntityTag;
    }
}
