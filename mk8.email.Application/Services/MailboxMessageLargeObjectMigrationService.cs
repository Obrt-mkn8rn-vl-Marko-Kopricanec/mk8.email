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
                .ToListAsync(cancellationToken);
            if (legacy.Count == 0)
                break;

            foreach (var email in legacy)
                await MigrateAsync(email, cancellationToken);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateAsync(EmailDB email, CancellationToken cancellationToken)
    {
        var rawMessage = email.RawMessage?.ToArray();
        rawMessage ??= HasCompleteAzureReference(email)
            ? await content.ReadAsync(email, cancellationToken)
            : MailboxMessageContentService.BuildLegacyRawMessage(email);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rawMessage));
        var ownsTransaction = database.Database.IsRelational();
        await using var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        LargeObjectWriteResult? written = null;
        var commitAttempted = false;
        try
        {
            await using var source = new MemoryStream(rawMessage, writable: false);
            written = await objects.PutIfAbsentAsync(
                MailboxMessageContentService.BuildObjectName(email.Id, hash),
                source,
                rawMessage.LongLength,
                hash,
                "message/rfc822",
                cancellationToken);
            MailboxMessageContentService.ApplyReference(email, written.Reference);
            MailboxMessageContentService.ApplySearchProjection(email, rawMessage);
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
                        "Could not roll back mailbox message migration for {EmailId}",
                        email.Id);
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
                        "Could not clean up mailbox migration object {ObjectName}",
                        written.Reference.ObjectName);
                }
            }
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

    private static bool HasCompleteAzureReference(EmailDB email) =>
        string.Equals(
            email.RawMessageObjectProvider,
            LargeObjectProviders.AzureBlob,
            StringComparison.Ordinal)
        && email.RawMessageObjectName is not null
        && email.RawMessageObjectSha256 is not null
        && email.RawMessageObjectEntityTag is not null;
}
