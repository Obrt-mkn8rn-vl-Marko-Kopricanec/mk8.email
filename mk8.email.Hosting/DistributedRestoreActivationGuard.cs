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
            await RequireReadyAsync(dataSource, cancellationToken);
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
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'public'
                        AND relation.relname = 'mk8_restore_state')
                """;
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
                return;
        }

        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT state FROM public.mk8_restore_state";
        await using var reader = await state.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.GetString(0) != "complete"
            || await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The distributed snapshot restore is incomplete; refuse to activate this database.");
        }
    }

    internal static async Task BeginRestoreAsync(
        NpgsqlDataSource dataSource,
        string databaseSha256,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var empty = connection.CreateCommand())
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
            if (await empty.ExecuteScalarAsync(cancellationToken) is not 0L)
            {
                throw new InvalidOperationException(
                    "The target PostgreSQL database is not empty.");
            }
        }

        await using (var create = connection.CreateCommand())
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
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task CompleteRestoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = """
            UPDATE public.mk8_restore_state
            SET state = 'complete', completed_at = now()
            WHERE id = 1 AND state = 'pending'
            """;
        if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The distributed restore state could not be completed.");
    }
}
