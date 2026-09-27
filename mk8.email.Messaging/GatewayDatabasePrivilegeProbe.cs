using Npgsql;
using mk8.email.DatabasePolicy;

namespace mk8.email.Messaging;

public static class GatewayDatabasePrivilegeProbe
{
    public static async Task ProbeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await RestrictedPostgresRolePolicy.RequireAsync(dataSource, "gateway", cancellationToken).ConfigureAwait(false);
        if (!await new PostgresApplicationTransportControl(dataSource)
                .IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The Gateway database role lacks a required messaging-table permission.");
    }
}
