using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.DatabasePolicy;

// Linked into the three PostgreSQL-only callers, with the same immutable SQL resource.
// Wake does not gain a dependency on Messaging, Hosting, Application, or Blob clients.
internal static class RestrictedPostgresRolePolicy
{
    private const string ResourceName = "mk8.email.RestrictedRolePolicy.sql";

    internal static async Task RequireAsync(NpgsqlDataSource source, string kind, CancellationToken cancellationToken)
    {
        var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await RequireAsync(connection, transaction, null, kind, requireReady: true, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RequireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string? role, string kind, bool requireReady, CancellationToken cancellationToken)
    {
        // Runtime probes must audit the authenticated login, not a resettable SET ROLE/session identity.
        // Only the operator transition supplies an explicit target role. Never trust a prior GUC.
        var selection = new NpgsqlCommand("""
            SELECT set_config('mk8.restricted_role', CASE
                WHEN @role IS NOT NULL THEN @role
                WHEN current_user = session_user AND EXISTS (
                    SELECT 1 FROM pg_stat_activity WHERE pid = pg_backend_pid() AND usename = current_user)
                    THEN current_user ELSE '' END, true),
                set_config('mk8.restricted_role_kind', @kind, true)
            """, connection, transaction);
        await using var selectionLifetime = selection.ConfigureAwait(false);
        selection.Parameters.AddWithValue("role", NpgsqlDbType.Text, role is null ? DBNull.Value : role);
        selection.Parameters.AddWithValue("kind", kind);
        await selection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var resource = typeof(RestrictedPostgresRolePolicy).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The immutable PostgreSQL role policy is missing.");
        await using var resourceLifetime = resource.ConfigureAwait(false);
        using var reader = new StreamReader(resource, Encoding.UTF8, leaveOpen: true);
        var script = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd();
        const string footer = "\n\\gset";
        if (!script.EndsWith(footer, StringComparison.Ordinal))
            throw new InvalidOperationException("The PostgreSQL role policy has an invalid script footer.");
        var sql = script[..^footer.Length];
        // The query is an embedded build asset, not mutable SQL supplied by a caller.
#pragma warning disable CA2100
        var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        await using var commandLifetime = command.ConfigureAwait(false);
        var result = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var resultLifetime = result.ConfigureAwait(false);
        if (!await result.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !result.GetBoolean(requireReady ? 0 : 1))
            throw new InvalidOperationException("The database role has unsafe ownership, delegation, defaults or routine authority.");
    }
}
