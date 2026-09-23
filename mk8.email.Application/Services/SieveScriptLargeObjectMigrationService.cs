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
            var legacy = await database.SieveScripts
                .Where(script => script.Content != null)
                .OrderBy(script => script.CreatedAt)
                .ThenBy(script => script.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (legacy.Count == 0)
                break;

            foreach (var script in legacy)
                await MigrateScriptAsync(script, cancellationToken);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateScriptAsync(
        SieveScriptDB script,
        CancellationToken cancellationToken)
    {
        var legacyContent = script.Content
            ?? throw new InvalidOperationException("The legacy Sieve script content is missing.");
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var marker = transactionEffects.Mark();
        var commitAttempted = false;
        try
        {
            await content.SetAsync(script, legacyContent, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            await transactionEffects.CommitAsync(marker);
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
                        "Could not roll back Sieve script migration for {ScriptId}",
                        script.Id);
                }
            }
            if (commitAttempted)
                transactionEffects.Discard(marker);
            else
                await transactionEffects.RollbackAsync(marker);
            throw;
        }
    }

    private async Task EnforceExternalStorageAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
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
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private bool IsPostgreSql() => string.Equals(
        database.Database.ProviderName,
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        StringComparison.Ordinal);
}
