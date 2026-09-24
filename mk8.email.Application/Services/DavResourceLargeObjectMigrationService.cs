using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class DavResourceLargeObjectMigrationService(
    EmailDbContext database,
    ILargeObjectStore objects,
    ILogger<DavResourceLargeObjectMigrationService> logger)
{
    private const int BatchSize = 50;
    private const long MigrationLockKey = 6_201_857_443_901_226_173;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DAV resource migration requires an Azure Blob-compatible object store.");
        }
        if (database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "DAV resource migration must own its database transactions.");
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
            var legacy = await database.DavResources
                .Where(resource => resource.Content != null)
                .OrderBy(resource => resource.CreatedAt)
                .ThenBy(resource => resource.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.Count == 0)
                break;

            for (var index = 0; index < legacy.Count; index++)
                await MigrateAsync(legacy[index], cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateAsync(
        DavResourceDB resource,
        CancellationToken cancellationToken)
    {
        var content = resource.Content
            ?? throw new InvalidOperationException("The legacy DAV resource content is missing.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (resource.SizeBytes != content.Length
            || !string.Equals(resource.Etag, hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Legacy DAV resource {resource.Id:D} has inconsistent integrity metadata.");
        }

        var ownsTransaction = database.Database.IsRelational();
        var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            LargeObjectWriteResult? written = null;
            var commitAttempted = false;
            try
            {
                var source = new MemoryStream(content, writable: false);
                await using var sourceLifetime = source.ConfigureAwait(false);
                written = await objects.PutIfAbsentAsync(
                    DavResourceContentService.BuildObjectName(resource.Id, hash),
                    source,
                    content.LongLength,
                    hash,
                    resource.ContentType,
                    cancellationToken).ConfigureAwait(false);
                DavResourceContentService.ApplyReference(resource, written.Reference);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        ApplicationServiceLog.DavMigrationRollbackFailed(
                            logger, rollbackException, resource.Id);
                    }
                }
                if (written is { Created: true } && !commitAttempted)
                {
                    try
                    {
                        await objects.DeleteIfMatchAsync(written.Reference, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        ApplicationServiceLog.DavMigrationCleanupFailed(
                            logger, cleanupException, written.Reference.ObjectName);
                    }
                }
                throw;
            }
        }
    }

    private async Task EnforceExternalStorageAsync(CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            try
            {
                await database.Database.ExecuteSqlRawAsync(
                    """
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conrelid = 'dav_resources'::regclass
                          AND conname = 'ck_dav_resources_external_storage'
                    ) THEN
                        ALTER TABLE dav_resources
                            ADD CONSTRAINT ck_dav_resources_external_storage CHECK (
                                content IS NULL
                                AND object_provider = 'azure-blob'
                                AND object_name IS NOT NULL
                                AND object_sha256 IS NOT NULL
                                AND object_etag IS NOT NULL) NOT VALID;
                    END IF;
                END
                $migration$;
                ALTER TABLE dav_resources
                    VALIDATE CONSTRAINT ck_dav_resources_external_storage;
                """,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private bool IsPostgreSql() => string.Equals(
        database.Database.ProviderName,
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        StringComparison.Ordinal);
}
