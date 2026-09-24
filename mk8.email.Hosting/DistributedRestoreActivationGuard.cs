using System.Data;
using Npgsql;

namespace mk8.email.Hosting;

/// <summary>
/// Prevents the Application Worker from starting and Gateway operations from
/// using a database whose Blob ETags have not been rebound after a restore.
/// </summary>
public static class DistributedRestoreActivationGuard
{
    public static async Task<bool> IsReadyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RequireReadyAsync(dataSource, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException
            or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task RequireReadyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var exists = connection.CreateCommand();
            await using (exists.ConfigureAwait(false))
            {
                exists.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'public'
                        AND relation.relname = 'mk8_restore_state')
                """;
                if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    return;
            }

            var state = connection.CreateCommand();
            await using (state.ConfigureAwait(false))
            {
                state.CommandText = "SELECT state FROM public.mk8_restore_state";
                var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using var readerLifetime = reader.ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || !string.Equals(reader.GetString(0), "complete", StringComparison.Ordinal)
                    || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The distributed snapshot restore is incomplete; refuse to activate this database.");
                }
            }
        }
    }

    internal static async Task BeginRestoreAsync(
        NpgsqlDataSource dataSource,
        string databaseSha256,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            var empty = connection.CreateCommand();
            await using (empty.ConfigureAwait(false))
            {
                empty.Transaction = transaction;
                empty.CommandText = """
                SELECT count(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                WHERE schema.nspname !~ '^pg_'
                    AND schema.nspname <> 'information_schema'
                    AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
                """;
                if (await empty.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 0L)
                {
                    throw new InvalidOperationException(
                        "The target PostgreSQL database is not empty.");
                }
            }

            var create = connection.CreateCommand();
            await using (create.ConfigureAwait(false))
            {
                create.Transaction = transaction;
                create.CommandText = """
                CREATE TABLE public.mk8_restore_state (
                    id smallint PRIMARY KEY CHECK (id = 1),
                    state text NOT NULL CHECK (state IN ('pending', 'complete')),
                    database_sha256 text NOT NULL,
                    manifest_sha256 text NOT NULL,
                    started_at timestamptz NOT NULL,
                    completed_at timestamptz NULL
                );
                INSERT INTO public.mk8_restore_state (
                    id, state, database_sha256, manifest_sha256, started_at)
                VALUES (1, 'pending', @database_sha256, @manifest_sha256, now());
                """;
                create.Parameters.AddWithValue("database_sha256", databaseSha256);
                create.Parameters.AddWithValue("manifest_sha256", manifestSha256);
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task CompleteRestoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var complete = connection.CreateCommand();
        await using (complete.ConfigureAwait(false))
        {
            complete.Transaction = transaction;
            complete.CommandText = """
            UPDATE public.mk8_restore_state
            SET state = 'complete', completed_at = now()
            WHERE id = 1 AND state = 'pending'
            """;
            if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The distributed restore state could not be completed.");
        }
    }
}
