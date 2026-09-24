using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class SieveScriptLargeObjectMigrationService(
    EmailDbContext database,
    ILargeObjectStore objects,
    SieveScriptContentService content,
    LargeObjectTransactionEffects transactionEffects,
    ILogger<SieveScriptLargeObjectMigrationService> logger)
{
    private const int BatchSize = 50;
    private const long MigrationLockKey = 6_201_857_443_901_226_174;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Sieve script migration requires an Azure Blob-compatible object store.");
        }
        if (database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Sieve script migration must own its database transactions.");
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
            var legacy = await database.SieveScripts
                .Where(script => script.Content != null)
                .OrderBy(script => script.CreatedAt)
                .ThenBy(script => script.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.Count == 0)
                break;

            for (var index = 0; index < legacy.Count; index++)
                await MigrateScriptAsync(legacy[index], cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateScriptAsync(
        SieveScriptDB script,
        CancellationToken cancellationToken)
    {
        var legacyContent = script.Content
            ?? throw new InvalidOperationException("The legacy Sieve script content is missing.");
        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var marker = transactionEffects.Mark();
            var commitAttempted = false;
            try
            {
                await content.SetAsync(script, legacyContent, cancellationToken).ConfigureAwait(false);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                await transactionEffects.CommitAsync(marker).ConfigureAwait(false);
            }
            catch
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    // Keep the migration failure primary if provider rollback also fails.
#pragma warning disable CA1031
                    catch (Exception rollbackException)
                    {
                        ApplicationServiceLog.SieveMigrationRollbackFailed(
                            logger, rollbackException, script.Id);
                    }
#pragma warning restore CA1031
                }
                if (commitAttempted)
                    transactionEffects.Discard(marker);
                else
                    await transactionEffects.RollbackAsync(marker).ConfigureAwait(false);
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
                        WHERE conrelid = 'sieve_scripts'::regclass
                          AND conname = 'ck_sieve_scripts_external_storage'
                    ) THEN
                        ALTER TABLE sieve_scripts
                            ADD CONSTRAINT ck_sieve_scripts_external_storage CHECK (
                                content IS NULL
                                AND size_bytes BETWEEN 1 AND 1048576
                                AND object_provider = 'azure-blob'
                                AND object_name IS NOT NULL
                                AND object_sha256 IS NOT NULL
                                AND object_etag IS NOT NULL) NOT VALID;
                    END IF;
                END
                $migration$;
                ALTER TABLE sieve_scripts
                    VALIDATE CONSTRAINT ck_sieve_scripts_external_storage;
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
