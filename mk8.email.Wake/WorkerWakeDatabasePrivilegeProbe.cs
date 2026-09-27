using Npgsql;
using mk8.email.DatabasePolicy;

namespace mk8.email.Wake;

internal static class WorkerWakeDatabasePrivilegeProbe
{
    public static async Task ProbeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await RestrictedPostgresRolePolicy.RequireAsync(dataSource, "wake", cancellationToken).ConfigureAwait(false);
    }
}
