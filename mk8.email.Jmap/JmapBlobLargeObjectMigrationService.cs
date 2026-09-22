using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
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
            await MigrateRowsAsync(cancellationToken);
            return;
        }

        var connection = database.Database.GetDbConnection();
        var closeConnection = connection.State == ConnectionState.Closed;
        var lockTaken = false;
        try
        {
            if (closeConnection)
                await database.Database.OpenConnectionAsync(cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_lock({MigrationLockKey})",
                cancellationToken);
            lockTaken = true;
            await MigrateRowsAsync(cancellationToken);
            await EnforceExternalStorageAsync(cancellationToken);
        }
        finally
        {
            try
            {
                if (lockTaken)
                {
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_unlock({MigrationLockKey})",
                        CancellationToken.None);
                }
            }
            finally
            {
                if (closeConnection)
                    await database.Database.CloseConnectionAsync();
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
                .ToListAsync(cancellationToken);
            if (legacy.Count == 0)
                break;

            foreach (var blob in legacy)
                await MigrateAsync(blob, cancellationToken);
            database.ChangeTracker.Clear();
        }
    }

    private async Task EnforceExternalStorageAsync(CancellationToken cancellationToken)
    {
        var ownsTransaction = database.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken)
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
                cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
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
        await using var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        LargeObjectWriteResult? written = null;
        var commitAttempted = false;
        try
        {
            await using var source = new MemoryStream(content, writable: false);
            written = await objects.PutIfAbsentAsync(
                objectName,
                source,
                content.LongLength,
                hash,
                blob.ContentType,
                cancellationToken);
            ApplyReference(blob, written.Reference);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogWarning(
                        rollbackException,
                        "Could not roll back failed JMAP blob migration for {BlobId}",
                        blob.Id);
                }
            }
            if (written is { Created: true } && !commitAttempted)
            {
                try
                {
                    await objects.DeleteIfMatchAsync(written.Reference, CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    logger.LogWarning(
                        cleanupException,
                        "Could not clean up JMAP migration object {ObjectName}",
                        written.Reference.ObjectName);
                }
            }
            throw;
        }
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
