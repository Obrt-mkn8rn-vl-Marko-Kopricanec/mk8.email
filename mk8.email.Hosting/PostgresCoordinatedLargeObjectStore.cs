using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Hosting;

/// <summary>
/// Preserves the underlying Azure Blob store's delete result while ensuring a
/// backup snapshot cannot observe a reference and lose its Blob before export.
/// </summary>
public sealed class PostgresCoordinatedLargeObjectStore(
    NpgsqlDataSource dataSource,
    ILargeObjectStore inner) : ILargeObjectStore
{
    public string Provider => inner.Provider;

    public Task<LargeObjectWriteResult> PutIfAbsentAsync(
        string objectName,
        Stream content,
        long length,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default) =>
        inner.PutIfAbsentAsync(
            objectName, content, length, sha256, contentType, cancellationToken);

    public Task CopyToAsync(
        LargeObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        inner.CopyToAsync(reference, destination, cancellationToken);

    public async Task<bool> DeleteIfMatchAsync(
        LargeObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT pg_advisory_xact_lock_shared(@lock_id)";
                command.Parameters.AddWithValue("lock_id", PostgresBlobDeletionBarrier.LockId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var deleted = await inner.DeleteIfMatchAsync(reference, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
    }
}
