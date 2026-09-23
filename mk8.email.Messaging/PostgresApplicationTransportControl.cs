using Npgsql;

namespace mk8.email.Messaging;

public sealed class PostgresApplicationTransportControl(NpgsqlDataSource dataSource)
    : IApplicationTransportControl
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT has_schema_privilege(current_user, 'public', 'USAGE')
                    AND has_table_privilege(current_user,
                        to_regclass('gateway_traffic_records'), 'SELECT')
                    AND has_table_privilege(current_user,
                        to_regclass('gateway_traffic_records'), 'INSERT')
                    AND has_table_privilege(current_user,
                        to_regclass('application_requests'), 'SELECT')
                    AND has_table_privilege(current_user,
                        to_regclass('application_requests'), 'INSERT')
                    AND has_table_privilege(current_user,
                        to_regclass('application_requests'), 'UPDATE')
                    AND has_table_privilege(current_user,
                        to_regclass('presentation_requests'), 'SELECT')
                    AND has_table_privilege(current_user,
                        to_regclass('presentation_requests'), 'INSERT')
                    AND has_table_privilege(current_user,
                        to_regclass('presentation_requests'), 'UPDATE')
                    AND has_table_privilege(current_user,
                        to_regclass('pop3_maildrop_leases'), 'SELECT')
                    AND has_table_privilege(current_user,
                        to_regclass('pop3_maildrop_leases'), 'INSERT')
                    AND has_table_privilege(current_user,
                        to_regclass('pop3_maildrop_leases'), 'UPDATE')
                    AND has_table_privilege(current_user,
                        to_regclass('pop3_maildrop_leases'), 'DELETE')
                """);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }
}
