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
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT pg_advisory_lock(@lock_id)";
                command.Parameters.AddWithValue("lock_id", LockId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return new ExclusiveLease(connection);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // This existing public return type is intentionally nested under its lock owner.
#pragma warning disable CA1034
    public sealed class ExclusiveLease : IAsyncDisposable
#pragma warning restore CA1034
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
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "SELECT pg_advisory_unlock(@lock_id)";
                    command.Parameters.AddWithValue("lock_id", LockId);
                    if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not true)
                        throw new InvalidOperationException("The Blob deletion barrier was not held.");
                }
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
