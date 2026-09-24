using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MailboxMessageLargeObjectMigrationService(
    EmailDbContext database,
    ILargeObjectStore objects,
    MailboxMessageContentService content,
    ILogger<MailboxMessageLargeObjectMigrationService> logger)
{
    private const int BatchSize = 10;
    private const long MigrationLockKey = 6_201_857_443_901_226_174;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mailbox message migration requires an Azure Blob-compatible object store.");
        }
        if (database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Mailbox message migration must own its database transactions.");
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
            var legacy = await database.Emails
                .Where(email => email.RawMessage != null
                    || email.RawMessageObjectProvider == null
                    || email.RawMessageObjectProvider != LargeObjectProviders.AzureBlob
                    || email.RawMessageObjectName == null
                    || email.RawMessageObjectSha256 == null
                    || email.RawMessageObjectEntityTag == null
                    || email.Body.Length > MailboxMessageContentService.MaximumSearchProjectionCharacters
                    || email.RawHeaders != null
                        && email.RawHeaders.Length
                            > MailboxMessageContentService.MaximumSearchProjectionCharacters)
                .OrderBy(email => email.ReceivedAt)
                .ThenBy(email => email.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.Count == 0)
                break;

            foreach (var email in legacy)
                await MigrateAsync(email, cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateAsync(EmailDB email, CancellationToken cancellationToken)
    {
        var rawMessage = email.RawMessage?.ToArray();
        rawMessage ??= HasCompleteAzureReference(email)
            ? await content.ReadAsync(email, cancellationToken)
.ConfigureAwait(false) : MailboxMessageContentService.BuildLegacyRawMessage(email);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rawMessage));
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
                var source = new MemoryStream(rawMessage, writable: false);
                await using var sourceLifetime = source.ConfigureAwait(false);
                written = await objects.PutIfAbsentAsync(
                    MailboxMessageContentService.BuildObjectName(email.Id, hash),
                    source,
                    rawMessage.LongLength,
                    hash,
                    "message/rfc822",
                    cancellationToken).ConfigureAwait(false);
                MailboxMessageContentService.ApplyReference(email, written.Reference);
                MailboxMessageContentService.ApplySearchProjection(email, rawMessage);
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
                        logger.LogWarning(
                            rollbackException,
                            "Could not roll back mailbox message migration for {EmailId}",
                            email.Id);
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
                        logger.LogWarning(
                            cleanupException,
                            "Could not clean up mailbox migration object {ObjectName}",
                            written.Reference.ObjectName);
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
                        WHERE conrelid = 'emails'::regclass
                          AND conname = 'ck_emails_external_storage'
                    ) THEN
                        ALTER TABLE emails
                            ADD CONSTRAINT ck_emails_external_storage CHECK (
                                raw_message IS NULL
                                AND raw_message_object_provider = 'azure-blob'
                                AND raw_message_object_name IS NOT NULL
                                AND raw_message_object_sha256 IS NOT NULL
                                AND raw_message_object_etag IS NOT NULL
                                AND char_length(body) <= 65536
                                AND (raw_headers IS NULL OR char_length(raw_headers) <= 65536)) NOT VALID;
                    END IF;
                END
                $migration$;
                ALTER TABLE emails
                    VALIDATE CONSTRAINT ck_emails_external_storage;
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

    private static bool HasCompleteAzureReference(EmailDB email) =>
        string.Equals(
            email.RawMessageObjectProvider,
            LargeObjectProviders.AzureBlob,
            StringComparison.Ordinal)
        && email.RawMessageObjectName is not null
        && email.RawMessageObjectSha256 is not null
        && email.RawMessageObjectEntityTag is not null;
}
