using Npgsql;

namespace mk8.email.Wake;

internal static class WorkerWakeDatabasePrivilegeProbe
{
#pragma warning disable MA0051 // Keep the privilege audit query intact for security review.
    public static async Task ProbeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var command = dataSource.CreateCommand(
            """
            WITH required_columns(table_name, column_name) AS (
                VALUES
                    ('application_requests', 'state'),
                    ('application_requests', 'lease_expires_at'),
                    ('application_requests', 'deadline_at'),
                    ('mail_queue_messages', 'state'),
                    ('mail_queue_messages', 'next_attempt_at'),
                    ('mail_queue_messages', 'lease_expires_at'),
                    ('jmap_push_subscriptions', 'expires_at'),
                    ('jmap_push_subscriptions', 'is_verified'),
                    ('jmap_push_subscriptions', 'next_push_at'),
                    ('jmap_push_subscriptions', 'user_id'),
                    ('jmap_push_subscriptions', 'last_pushed_change'),
                    ('users', 'id'),
                    ('users', 'is_active'),
                    ('inboxes', 'id'),
                    ('inboxes', 'owner_id'),
                    ('inboxes', 'alias_for_inbox_id'),
                    ('inboxes', 'name'),
                    ('inboxes', 'address_id'),
                    ('addresses', 'id'),
                    ('addresses', 'company_id'),
                    ('addresses', 'is_active'),
                    ('companies', 'id'),
                    ('companies', 'is_active'),
                    ('jmap_changes', 'account_id'),
                    ('jmap_changes', 'sequence')
            )
            SELECT NOT EXISTS (
                    SELECT 1 FROM required_columns AS required
                    LEFT JOIN pg_class AS relation
                        ON relation.oid = to_regclass('public.' || required.table_name)
                    LEFT JOIN pg_attribute AS attribute
                        ON attribute.attrelid = relation.oid
                            AND attribute.attname = required.column_name
                            AND attribute.attnum > 0 AND NOT attribute.attisdropped
                    WHERE relation.oid IS NULL OR attribute.attnum IS NULL
                        OR NOT has_column_privilege(current_user, relation.oid,
                            attribute.attname, 'SELECT'))
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
                                    OR has_table_privilege(current_user,
                                        relation.oid, 'SELECT')))
                            OR (relation.relkind = 'S'
                                AND has_sequence_privilege(current_user, relation.oid,
                                    'USAGE,SELECT,UPDATE'))))
                AND NOT EXISTS (
                    SELECT 1 FROM pg_class AS relation
                    JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
                    JOIN pg_attribute AS attribute ON attribute.attrelid = relation.oid
                    WHERE schema.nspname !~ '^pg_'
                        AND schema.nspname <> 'information_schema'
                        AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                        AND attribute.attnum > 0 AND NOT attribute.attisdropped
                        AND has_column_privilege(current_user, relation.oid,
                            attribute.attname, 'SELECT')
                        AND NOT EXISTS (
                            SELECT 1 FROM required_columns AS required
                            WHERE schema.nspname = 'public'
                                AND relation.relname = required.table_name
                                AND attribute.attname = required.column_name))
            """);
        await using var commandLifetime = command.ConfigureAwait(false);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException(
                "The Worker wake database role needs exactly its read-only scheduling permissions.");
        }
    }
#pragma warning restore MA0051
}
