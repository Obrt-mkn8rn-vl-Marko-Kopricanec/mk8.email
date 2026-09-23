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
        if (objects.Provider != LargeObjectProviders.AzureBlob)
            throw new InvalidOperationException("Distributed storage must use the Azure Blob protocol.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.Transaction = transaction;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        long count = 0;
        await foreach (var row in DistributedBlobReferenceInventory.EnumerateAsync(
                           connection, transaction, cancellationToken))
        {
            using var hash = SHA256.Create();
            await using (var sink = new CryptoStream(
                             Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true))
            {
                await objects.CopyToAsync(row.Reference, sink, cancellationToken);
                sink.FlushFinalBlock();
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    hash.Hash!, Convert.FromHexString(row.Reference.Sha256)))
            {
                throw new InvalidOperationException(
                    $"Large-object content hash mismatch at {row.Source}, row {row.RowId}.");
            }
            count++;
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
    }
}
