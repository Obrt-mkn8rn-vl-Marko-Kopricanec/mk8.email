using Npgsql;

namespace mk8.email.Wake;

public static class WorkerWakeDatabasePrivilegeProbe
{
    public static async Task ProbeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var command = dataSource.CreateCommand(
            """
            WITH required_tables(name) AS (
                VALUES ('application_requests'), ('mail_queue_messages'),
                    ('jmap_push_subscriptions'), ('jmap_changes'),
                    ('users'), ('inboxes'), ('addresses'), ('companies')
            )
            SELECT NOT EXISTS (
                    SELECT 1 FROM required_tables
                    WHERE to_regclass('public.' || name) IS NULL
                        OR NOT has_table_privilege(current_user,
                            to_regclass('public.' || name), 'SELECT'))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_roles
                    WHERE rolname = current_user
                        AND (rolsuper OR rolcreatedb OR rolcreaterole
                            OR rolreplication OR rolbypassrls OR rolinherit))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_auth_members
                    WHERE member = (SELECT oid FROM pg_roles WHERE rolname = current_user))
                AND has_database_privilege(
                    current_user, current_database(), 'CONNECT')
                AND NOT has_database_privilege(
                    current_user, current_database(), 'CREATE')
                AND NOT has_database_privilege(
                    current_user, current_database(), 'TEMPORARY')
                AND has_schema_privilege(current_user, 'public', 'USAGE')
                AND NOT EXISTS (
                    SELECT 1 FROM pg_namespace AS schema
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND (schema.nspowner = (
                            SELECT oid FROM pg_roles WHERE rolname = current_user)
                            OR has_schema_privilege(current_user, schema.oid, 'CREATE')))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_proc AS routine
                    JOIN pg_namespace AS schema ON schema.oid = routine.pronamespace
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND (routine.proowner = (
                            SELECT oid FROM pg_roles WHERE rolname = current_user)
                            OR (routine.prosecdef
                                AND has_function_privilege(current_user,
                                    routine.oid, 'EXECUTE'))))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND (relation.relowner = (
                            SELECT oid FROM pg_roles WHERE rolname = current_user)
                            OR (relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                                AND (has_table_privilege(current_user, relation.oid,
                                        'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                                    OR has_any_column_privilege(current_user,
                                        relation.oid, 'INSERT,UPDATE,REFERENCES')
                                    OR ((has_table_privilege(current_user, relation.oid,
                                            'SELECT')
                                        OR has_any_column_privilege(current_user,
                                            relation.oid, 'SELECT'))
                                        AND NOT (schema.nspname = 'public'
                                            AND relation.relname IN (
                                                SELECT name FROM required_tables)))))
                            OR (relation.relkind = 'S'
                                AND has_sequence_privilege(current_user, relation.oid,
                                    'USAGE,SELECT,UPDATE'))))
            """);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "The Worker wake database role needs exactly its read-only scheduling permissions.");
        }
    }
}
