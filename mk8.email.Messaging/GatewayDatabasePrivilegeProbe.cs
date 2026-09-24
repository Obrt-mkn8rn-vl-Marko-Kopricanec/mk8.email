using Npgsql;

namespace mk8.email.Messaging;

public static class GatewayDatabasePrivilegeProbe
{
    public static async Task ProbeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (!await new PostgresApplicationTransportControl(dataSource)
                .IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Gateway database role lacks a required messaging-table permission.");
        }

        var command = dataSource.CreateCommand(PrivilegeQuery);
        await using var commandLifetime = command.ConfigureAwait(false);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException(
                "The Gateway database role has privileges outside the presentation control plane.");
        }
    }

    private const string PrivilegeQuery =
            """
            SELECT NOT EXISTS (
                    SELECT 1 FROM pg_roles
                    WHERE rolname = current_user
                        AND (rolsuper OR rolcreatedb OR rolcreaterole
                            OR rolreplication OR rolbypassrls))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_auth_members
                    WHERE member = (SELECT oid FROM pg_roles WHERE rolname = current_user))
                AND NOT has_database_privilege(
                    current_user, current_database(), 'CREATE')
                AND NOT has_database_privilege(
                    current_user, current_database(), 'TEMPORARY')
                AND NOT has_table_privilege(current_user,
                    to_regclass('public.gateway_traffic_records'), 'UPDATE')
                AND NOT has_table_privilege(current_user,
                    to_regclass('public.gateway_traffic_records'), 'DELETE')
                AND NOT has_table_privilege(current_user,
                    to_regclass('public.application_requests'), 'DELETE')
                AND NOT has_table_privilege(current_user,
                    to_regclass('public.presentation_requests'), 'DELETE')
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'public'
                        AND relation.relname IN (
                            'gateway_traffic_records', 'application_requests',
                            'presentation_requests', 'pop3_maildrop_leases')
                        AND (has_table_privilege(current_user, relation.oid, 'TRUNCATE')
                            OR has_table_privilege(current_user, relation.oid, 'REFERENCES')
                            OR has_table_privilege(current_user, relation.oid, 'TRIGGER')))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'public'
                        AND relation.relname = 'mk8_restore_state'
                        AND (NOT has_column_privilege(
                                current_user, relation.oid, 'state', 'SELECT')
                            OR has_table_privilege(current_user, relation.oid,
                                'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                            OR has_any_column_privilege(current_user, relation.oid,
                                'INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    JOIN pg_attribute AS attribute ON attribute.attrelid = relation.oid
                    WHERE schema.nspname = 'public'
                        AND relation.relname = 'mk8_restore_state'
                        AND attribute.attnum > 0 AND NOT attribute.attisdropped
                        AND attribute.attname <> 'state'
                        AND has_column_privilege(current_user, relation.oid,
                            attribute.attname, 'SELECT'))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_namespace AS schema
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND has_schema_privilege(current_user, schema.oid, 'CREATE'))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND relation.relowner = (
                            SELECT oid FROM pg_roles WHERE rolname = current_user))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                        AND NOT (schema.nspname = 'public'
                            AND relation.relname IN (
                                'gateway_traffic_records', 'application_requests',
                                'presentation_requests', 'pop3_maildrop_leases',
                                'mk8_restore_state'))
                        AND (has_table_privilege(current_user, relation.oid, 'SELECT')
                            OR has_table_privilege(current_user, relation.oid, 'INSERT')
                            OR has_table_privilege(current_user, relation.oid, 'UPDATE')
                            OR has_table_privilege(current_user, relation.oid, 'DELETE')))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND relation.relkind = 'S'
                        AND (has_sequence_privilege(current_user, relation.oid, 'USAGE')
                            OR has_sequence_privilege(current_user, relation.oid, 'SELECT')
                            OR has_sequence_privilege(current_user, relation.oid, 'UPDATE')))
            """;
}
