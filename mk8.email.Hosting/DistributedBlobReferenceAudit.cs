using System.Data;
using System.Security.Cryptography;
using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Hosting;

/// <summary>
/// Read-only integrity check for all database-referenced Azure Blob objects.
/// This does not replace a consistent backup or protect against concurrent deletion.
/// </summary>
public static class DistributedBlobReferenceAudit
{
    public static async Task<long> AuditAsync(
        NpgsqlDataSource dataSource,
        ILargeObjectStore objects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(objects);
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Distributed storage must use the Azure Blob protocol.");

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            var readOnly = connection.CreateCommand();
            await using (readOnly.ConfigureAwait(false))
            {
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            long count = 0;
            await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                               connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                using var hash = SHA256.Create();
                var sink = new CryptoStream(
                                 Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
                await using (sink.ConfigureAwait(false))
                {
                    await objects.CopyToAsync(row.Reference, sink, cancellationToken).ConfigureAwait(false);
                    await sink.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!CryptographicOperations.FixedTimeEquals(
                        hash.Hash!, Convert.FromHexString(row.Reference.Sha256)))
                {
                    throw new InvalidOperationException(
                        $"Large-object content hash mismatch at {row.Source}, row {row.RowId}.");
                }
                count++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
    }
}
