using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MailQueueLargeObjectMigrationService(
    EmailDbContext database,
    ILargeObjectStore objects,
    ILogger<MailQueueLargeObjectMigrationService> logger)
{
    private const int BatchSize = 50;
    private const long MigrationLockKey = 3_415_682_194_307_812_221;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mail queue migration requires an Azure Blob-compatible object store.");
        }
        if (database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Mail queue migration must own its database transactions.");
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
            var legacy = await database.MailQueueMessages
                .Where(message => message.RawMessage != null)
                .OrderBy(message => message.ReceivedAt)
                .ThenBy(message => message.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.Count == 0)
                break;

            foreach (var message in legacy)
                await MigrateAsync(message, cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
        }
    }

    private async Task MigrateAsync(
        MailQueueMessageDB message,
        CancellationToken cancellationToken)
    {
        var rawMessage = message.RawMessage
            ?? throw new InvalidOperationException("The legacy queue content is missing.");
        if (rawMessage.Any(character => character > byte.MaxValue))
        {
            throw new InvalidOperationException(
                $"Legacy queue message {message.Id:D} is not a mail wire byte representation.");
        }
        var content = MailWireEncoding.Instance.GetBytes(rawMessage);
        if (message.RawMessageSizeBytes != content.LongLength)
        {
            throw new InvalidOperationException(
                $"Legacy queue message {message.Id:D} has inconsistent length metadata.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
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
                    MailQueueContentService.BuildObjectName(message.Id),
                    source,
                    content.LongLength,
                    hash,
                    "message/rfc822",
                    cancellationToken).ConfigureAwait(false);
                MailQueueContentService.ApplyReference(message, written.Reference);
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
                            "Could not roll back queue content migration for {QueueId}",
                            message.Id);
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
                            "Could not clean up queue migration object {ObjectName}",
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
                        WHERE conrelid = 'mail_queue_messages'::regclass
                          AND conname = 'ck_mail_queue_messages_external_storage'
                    ) THEN
                        ALTER TABLE mail_queue_messages
                            ADD CONSTRAINT ck_mail_queue_messages_external_storage CHECK (
                                raw_message IS NULL
                                AND raw_message_object_provider = 'azure-blob'
                                AND raw_message_object_name IS NOT NULL
                                AND raw_message_object_sha256 IS NOT NULL
                                AND raw_message_object_etag IS NOT NULL) NOT VALID;
                    END IF;
                END
                $migration$;
                ALTER TABLE mail_queue_messages
                    VALIDATE CONSTRAINT ck_mail_queue_messages_external_storage;
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
