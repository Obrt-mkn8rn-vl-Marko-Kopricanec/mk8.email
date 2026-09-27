using mk8.email.Contracts.Messaging;
using Npgsql;
using mk8.email.DatabasePolicy;

namespace mk8.email.Hosting;

/// <summary>Operator-only coordination of receipt scheduling grants and empty-schema rollback.</summary>
public static class WorkerWakeSchemaTransition
{
    private const long PermissionLockId = 5_563_539_125_731_845_289;

    public static async Task<WorkerWakeSchemaState> ReadAsync(
        NpgsqlDataSource source, string wakeRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateRoleName(wakeRole);
        var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ValidateRoleAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            return WorkerWakeSchemaState.Legacy;
        await RequireSchedulingGrantsAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
        return WorkerWakeSchemaState.Receipts;
    }

    public static async Task EnableAsync(
        NpgsqlDataSource source, string wakeRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateRoleName(wakeRole);
        var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ValidateRoleAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Prepare the receipt schema before enabling Wake scheduling.");
        await ChangeGrantsAsync(connection, transaction, wakeRole, grant: true, cancellationToken).ConfigureAwait(false);
        await RequireSchedulingGrantsAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task RestoreAsync(
        NpgsqlDataSource source, string wakeRole, WorkerWakeSchemaState prior,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateRoleName(wakeRole);
        if (!Enum.IsDefined(prior))
            throw new ArgumentOutOfRangeException(nameof(prior));
        // A legacy schema rollback must not race a database/Blob snapshot.
        var lease = await PostgresBlobDeletionBarrier.AcquireExclusiveAsync(source, cancellationToken).ConfigureAwait(false);
        await using var leaseLifetime = lease.ConfigureAwait(false);
        var connection = lease.Connection;
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ValidateRoleAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
        if (prior == WorkerWakeSchemaState.Receipts)
        {
            await RequireSchedulingGrantsAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
            return;
        }
        await RequireLegacyQueueAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (await TableExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            await DropEmptyReceiptTableAsync(connection, transaction, wakeRole, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@id)", connection, transaction);
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", PermissionLockId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var command = new NpgsqlCommand("SELECT to_regclass('public.application_operation_receipts') IS NOT NULL", connection, transaction);
        await using var commandLifetime = command.ConfigureAwait(false);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
    }

    private static async Task ValidateRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string role, CancellationToken token)
    {
        await RestrictedPostgresRolePolicy.RequireAsync(connection, transaction, role, "wake", requireReady: false, token).ConfigureAwait(false);
    }

    private static async Task RequireSchedulingGrantsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string role, CancellationToken token)
    {
        var command = new NpgsqlCommand("""
            SELECT has_column_privilege(@role, 'public.application_operation_receipts', 'effects_pending', 'SELECT')
                AND has_column_privilege(@role, 'public.application_operation_receipts', 'effects_retry_at', 'SELECT')
                AND NOT has_table_privilege(@role, 'public.application_operation_receipts', 'SELECT')
                AND NOT EXISTS (
                    SELECT 1 FROM pg_attribute a WHERE a.attrelid = 'public.application_operation_receipts'::regclass
                        AND a.attnum > 0 AND NOT a.attisdropped
                        AND a.attname NOT IN ('effects_pending', 'effects_retry_at')
                        AND has_column_privilege(@role, a.attrelid, a.attname, 'SELECT'))
                AND NOT has_any_column_privilege(@role, 'public.application_operation_receipts', 'INSERT,UPDATE,REFERENCES')
                AND NOT has_table_privilege(@role, 'public.application_operation_receipts', 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
                AND NOT EXISTS (
                    SELECT 1 FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) privilege
                    WHERE a.attrelid = 'public.application_operation_receipts'::regclass
                        AND privilege.grantee = (SELECT oid FROM pg_roles WHERE rolname = @role)
                        AND privilege.is_grantable)
            """, connection, transaction);
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("role", role);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("The receipt schema does not have its exact Wake scheduling permissions.");
    }

    private static async Task ChangeGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string role, bool grant, CancellationToken token)
    {
        using var identifiers = new NpgsqlCommandBuilder();
        // Only the fixed two-column DDL varies; the validated identifier is quoted, never SQL data.
#pragma warning disable CA2100
        var sql = grant
            ? $"GRANT SELECT (effects_pending, effects_retry_at) ON public.application_operation_receipts TO {identifiers.QuoteIdentifier(role)}"
            : $"REVOKE SELECT (effects_pending, effects_retry_at) ON public.application_operation_receipts FROM {identifiers.QuoteIdentifier(role)}";
        var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        await using var commandLifetime = command.ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task RequireLegacyQueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var tableLock = new NpgsqlCommand("LOCK TABLE public.application_requests, public.presentation_requests IN SHARE ROW EXCLUSIVE MODE", connection, transaction);
        await using var lockLifetime = tableLock.ConfigureAwait(false);
        await tableLock.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM public.application_requests
                WHERE state IN ('pending', 'processing') AND operation = @operation)
            """, connection, transaction);
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("operation", ApplicationOperations.JmapBatchExecute);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true)
            throw new InvalidOperationException("New-contract JMAP work prevents rollback to a pre-receipt Worker.");
    }

    private static async Task DropEmptyReceiptTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string role, CancellationToken token)
    {
        var tableLock = new NpgsqlCommand("LOCK TABLE public.application_operation_receipts IN ACCESS EXCLUSIVE MODE", connection, transaction);
        await using var lockLifetime = tableLock.ConfigureAwait(false);
        await tableLock.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM public.application_operation_receipts)", connection, transaction);
        await using var commandLifetime = command.ConfigureAwait(false);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true)
            throw new InvalidOperationException("Committed operation receipts prevent destructive legacy rollback.");
        await ChangeGrantsAsync(connection, transaction, role, grant: false, token).ConfigureAwait(false);
        var drop = new NpgsqlCommand("DROP TABLE public.application_operation_receipts", connection, transaction);
        await using var dropLifetime = drop.ConfigureAwait(false);
        await drop.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void ValidateRoleName(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.Length is < 1 or > 63 || role[0] is not (>= 'a' and <= 'z')
            || role.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("A bounded lowercase PostgreSQL role identifier is required.", nameof(role));
    }
}
