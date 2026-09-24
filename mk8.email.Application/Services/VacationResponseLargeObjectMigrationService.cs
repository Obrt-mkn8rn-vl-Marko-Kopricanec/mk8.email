using System.Data;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Application.Services;

public sealed class VacationResponseLargeObjectMigrationService(
    EmailDbContext database,
    ILargeObjectStore objects,
    VacationResponseContentService content,
    LargeObjectTransactionEffects transactionEffects)
{
    private const int BatchSize = 50;
    private const long MigrationLockKey = 6_201_857_443_901_226_175;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Vacation response migration requires Azure Blob-compatible storage.");
        if (database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Vacation response migration must own its database transactions.");

        if (!string.Equals(database.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
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
            var legacy = await database.JmapVacationResponses
                .Where(response => response.TextBody != null || response.HtmlBody != null)
                .OrderBy(response => response.AccountId)
                .Select(response => response.AccountId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.Count == 0)
                break;

            for (var index = 0; index < legacy.Count; index++)
                await MigrateResponseAsync(legacy[index], cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateResponseAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var marker = transactionEffects.Mark();
            var commitAttempted = false;
            try
            {
                if (transaction is not null)
                {
                    await database.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM jmap_vacation_responses WHERE account_id = {accountId} FOR UPDATE",
                        cancellationToken).ConfigureAwait(false);
                }
                var response = await database.JmapVacationResponses.FirstOrDefaultAsync(
                    row => row.AccountId == accountId,
                    cancellationToken).ConfigureAwait(false);
                if (response is null || response.TextBody is null && response.HtmlBody is null)
                {
                    if (transaction is not null)
                    {
                        commitAttempted = true;
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                var textBody = response.TextBody;
                var htmlBody = response.HtmlBody;
                await content.SetAsync(
                    response, textBody, htmlBody, cancellationToken).ConfigureAwait(false);
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
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
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
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await database.Database.ExecuteSqlRawAsync(
                    """
                    DO $migration$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'jmap_vacation_responses'::regclass
                              AND conname = 'ck_jmap_vacation_external_storage'
                        ) THEN
                            ALTER TABLE jmap_vacation_responses
                                ADD CONSTRAINT ck_jmap_vacation_external_storage CHECK (
                                    text_body IS NULL AND html_body IS NULL
                                    AND (
                                        (body_size_bytes = 0
                                         AND body_object_provider IS NULL
                                         AND body_object_name IS NULL
                                         AND body_object_sha256 IS NULL
                                         AND body_object_etag IS NULL)
                                        OR
                                        (body_size_bytes > 0
                                         AND body_object_provider IS NOT DISTINCT FROM 'azure-blob'
                                         AND body_object_name IS NOT NULL
                                         AND body_object_sha256 IS NOT NULL
                                         AND body_object_etag IS NOT NULL)
                                    )) NOT VALID;
                        END IF;
                    END
                    $migration$;
                    ALTER TABLE jmap_vacation_responses
                        VALIDATE CONSTRAINT ck_jmap_vacation_external_storage;
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
}
