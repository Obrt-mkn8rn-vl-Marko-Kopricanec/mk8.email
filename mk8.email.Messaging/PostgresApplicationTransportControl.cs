using Npgsql;

namespace mk8.email.Messaging;

public sealed class PostgresApplicationTransportControl(NpgsqlDataSource dataSource)
    : IApplicationTransportControl
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        PostgresMessagingSchema.EnsureAsync(dataSource, cancellationToken);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT to_regclass('application_requests') IS NOT NULL "
                + "AND to_regclass('gateway_traffic_records') IS NOT NULL");
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }
}
