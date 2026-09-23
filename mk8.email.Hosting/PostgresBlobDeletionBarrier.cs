using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Hosting;

/// <summary>
/// Coordinates physical Blob deletion with a database/Blob export. An exporter must
/// acquire the exclusive session lease before creating its PostgreSQL snapshot and
/// hold it until every object in that snapshot has been copied and verified.
/// </summary>
public static class PostgresBlobDeletionBarrier
{
    public const long LockId = 0x4D4B38424C4F4244;

    public static async Task<ExclusiveLease> AcquireExclusiveAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(@lock_id)";
            command.Parameters.AddWithValue("lock_id", LockId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new ExclusiveLease(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public sealed class ExclusiveLease : IAsyncDisposable
    {
        private NpgsqlConnection? _connection;

        internal ExclusiveLease(NpgsqlConnection connection) => _connection = connection;

        public NpgsqlConnection Connection => _connection
            ?? throw new ObjectDisposedException(nameof(ExclusiveLease));

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@lock_id)";
                command.Parameters.AddWithValue("lock_id", LockId);
                if (await command.ExecuteScalarAsync() is not true)
                    throw new InvalidOperationException("The Blob deletion barrier was not held.");
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}

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
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock_shared(@lock_id)";
            command.Parameters.AddWithValue("lock_id", PostgresBlobDeletionBarrier.LockId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var deleted = await inner.DeleteIfMatchAsync(reference, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
