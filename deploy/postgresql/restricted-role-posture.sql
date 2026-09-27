-- The caller sets mk8.restricted_role transaction-locally from its own trusted role selection.
-- PUBLIC's built-in function EXECUTE/type USAGE defaults remain available; explicit
-- future data privileges and authority delegation to a restricted role are forbidden.
WITH wake_columns(table_name, column_name) AS (
    VALUES ('application_requests', 'state'), ('application_requests', 'lease_expires_at'),
        ('application_requests', 'deadline_at'), ('application_operation_receipts', 'effects_pending'),
        ('application_operation_receipts', 'effects_retry_at'), ('mail_queue_messages', 'state'),
        ('mail_queue_messages', 'next_attempt_at'), ('mail_queue_messages', 'lease_expires_at'),
        ('jmap_push_subscriptions', 'expires_at'), ('jmap_push_subscriptions', 'is_verified'),
        ('jmap_push_subscriptions', 'next_push_at'), ('jmap_push_subscriptions', 'user_id'),
        ('jmap_push_subscriptions', 'last_pushed_change'), ('users', 'id'), ('users', 'is_active'),
        ('inboxes', 'id'), ('inboxes', 'owner_id'), ('inboxes', 'alias_for_inbox_id'),
        ('inboxes', 'name'), ('inboxes', 'address_id'), ('addresses', 'id'),
        ('addresses', 'company_id'), ('addresses', 'is_active'), ('companies', 'id'),
        ('companies', 'is_active'), ('jmap_changes', 'account_id'), ('jmap_changes', 'sequence')
), gateway_tables(table_name, privilege) AS (
    VALUES ('gateway_traffic_records', 'SELECT'), ('gateway_traffic_records', 'INSERT'),
        ('application_requests', 'SELECT'), ('application_requests', 'INSERT'), ('application_requests', 'UPDATE'),
        ('presentation_requests', 'SELECT'), ('presentation_requests', 'INSERT'), ('presentation_requests', 'UPDATE'),
        ('pop3_maildrop_leases', 'SELECT'), ('pop3_maildrop_leases', 'INSERT'),
        ('pop3_maildrop_leases', 'UPDATE'), ('pop3_maildrop_leases', 'DELETE')
), restricted AS (
    SELECT * FROM pg_roles WHERE rolname = current_setting('mk8.restricted_role', true)
), authority AS (
    SELECT r.oid, (current_setting('mk8.restricted_role_kind', true) IN ('gateway', 'wake')
        AND r.rolcanlogin
        AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole
            OR r.rolreplication OR r.rolbypassrls OR r.rolinherit)
        AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = r.oid)
        AND NOT has_database_privilege(r.oid, current_database(), 'CREATE,TEMPORARY')
        AND NOT EXISTS (SELECT 1 FROM pg_database WHERE datdba = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_class WHERE relowner = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_proc WHERE proowner = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_type WHERE typowner = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_largeobject_metadata WHERE lomowner = r.oid)
        AND NOT EXISTS (SELECT 1 FROM (
            SELECT lanowner AS owner FROM pg_language
            UNION ALL SELECT collowner FROM pg_collation
            UNION ALL SELECT conowner FROM pg_conversion
            UNION ALL SELECT evtowner FROM pg_event_trigger
            UNION ALL SELECT extowner FROM pg_extension
            UNION ALL SELECT opcowner FROM pg_opclass
            UNION ALL SELECT oprowner FROM pg_operator
            UNION ALL SELECT opfowner FROM pg_opfamily
            UNION ALL SELECT pubowner FROM pg_publication
            UNION ALL SELECT stxowner FROM pg_statistic_ext
            UNION ALL SELECT subowner FROM pg_subscription
            UNION ALL SELECT cfgowner FROM pg_ts_config
            UNION ALL SELECT dictowner FROM pg_ts_dict
        ) owned WHERE owner = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_tablespace
            WHERE spcowner = r.oid OR has_tablespace_privilege(r.oid, oid, 'CREATE'))
        AND NOT EXISTS (SELECT 1 FROM pg_namespace n
            WHERE has_schema_privilege(r.oid, n.oid, 'CREATE'))
        AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                AND has_table_privilege(r.oid, c.oid, 'MAINTAIN'))
        AND NOT EXISTS (SELECT 1 FROM pg_proc p
            WHERE p.prosecdef AND has_function_privilege(r.oid, p.oid, 'EXECUTE'))
        AND NOT EXISTS (SELECT 1 FROM pg_foreign_data_wrapper
            WHERE fdwowner = r.oid OR has_foreign_data_wrapper_privilege(r.oid, oid, 'USAGE'))
        AND NOT EXISTS (SELECT 1 FROM pg_foreign_server
            WHERE srvowner = r.oid OR has_server_privilege(r.oid, oid, 'USAGE'))
        AND NOT EXISTS (SELECT 1 FROM pg_largeobject_metadata l
            CROSS JOIN LATERAL aclexplode(l.lomacl) a WHERE a.grantee IN (r.oid, 0))
        -- Ordinary catalog reads remain available, but explicit system-object grants,
        -- effective catalog writes, and sensitive credential/statistics/body reads do not.
        AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL aclexplode(c.relacl) a
            WHERE (n.nspname ~ '^pg_' OR n.nspname = 'information_schema') AND a.grantee = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_attribute attribute
            JOIN pg_class c ON c.oid = attribute.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL aclexplode(attribute.attacl) a
            WHERE (n.nspname ~ '^pg_' OR n.nspname = 'information_schema') AND a.grantee = r.oid)
        AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE (n.nspname ~ '^pg_' OR n.nspname = 'information_schema')
                AND CASE WHEN c.relkind IN ('r', 'p', 'v', 'm', 'f') THEN
                    has_table_privilege(r.oid, c.oid, 'INSERT,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
                    OR has_any_column_privilege(r.oid, c.oid, 'INSERT,REFERENCES')
                    -- PostgreSQL permits PUBLIC UPDATE of this built-in view, which
                    -- changes only parameters the caller itself is authorized to set.
                    OR ((has_table_privilege(r.oid, c.oid, 'UPDATE')
                        OR has_any_column_privilege(r.oid, c.oid, 'UPDATE'))
                        AND NOT (n.nspname = 'pg_catalog' AND c.relname = 'pg_settings')) ELSE false END)
        AND NOT EXISTS (SELECT 1 FROM (VALUES ('pg_authid'), ('pg_shadow'), ('pg_statistic'),
            ('pg_statistic_ext_data'), ('pg_user_mapping'), ('pg_largeobject')) private(table_name)
            WHERE has_any_column_privilege(r.oid, to_regclass('pg_catalog.' || private.table_name), 'SELECT'))
        AND NOT has_column_privilege(r.oid, 'pg_catalog.pg_subscription', 'subconninfo', 'SELECT')
        AND NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'),
                ('TRUNCATE'), ('REFERENCES'), ('TRIGGER'), ('MAINTAIN')) p(privilege)
            WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                AND has_table_privilege(r.oid, c.oid, p.privilege)
                AND NOT (current_setting('mk8.restricted_role_kind', true) = 'gateway'
                    AND n.nspname = 'public' AND EXISTS (SELECT 1 FROM gateway_tables g
                        WHERE g.table_name = c.relname AND g.privilege = p.privilege)))
        AND NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute attribute ON attribute.attrelid = c.oid AND attribute.attnum > 0 AND NOT attribute.attisdropped
            CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) p(privilege)
            WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                AND has_column_privilege(r.oid, c.oid, attribute.attname, p.privilege)
                AND NOT (n.nspname = 'public' AND (
                    (current_setting('mk8.restricted_role_kind', true) = 'gateway' AND (
                        EXISTS (SELECT 1 FROM gateway_tables g
                            WHERE g.table_name = c.relname AND g.privilege = p.privilege)
                        OR (c.relname = 'mk8_restore_state' AND attribute.attname = 'state' AND p.privilege = 'SELECT')))
                    OR (current_setting('mk8.restricted_role_kind', true) = 'wake' AND p.privilege = 'SELECT'
                        AND EXISTS (SELECT 1 FROM wake_columns w
                            WHERE w.table_name = c.relname AND w.column_name = attribute.attname)))))
        AND NOT EXISTS (
            SELECT 1 FROM pg_class c WHERE c.relkind = 'S'
                AND CASE WHEN c.relkind = 'S'
                    THEN has_sequence_privilege(r.oid, c.oid, 'USAGE,SELECT,UPDATE') ELSE false END)
        AND NOT EXISTS (
            SELECT 1 FROM pg_default_acl d CROSS JOIN LATERAL aclexplode(d.defaclacl) a
            WHERE a.grantee = r.oid OR (a.grantee = 0
                AND NOT ((d.defaclobjtype = 'f' AND a.privilege_type = 'EXECUTE')
                    OR (d.defaclobjtype = 'T' AND a.privilege_type = 'USAGE'))))
        AND NOT EXISTS (
            SELECT 1 FROM pg_parameter_acl p CROSS JOIN LATERAL aclexplode(p.paracl) a
            WHERE a.grantee IN (r.oid, 0))
        AND NOT EXISTS (
            SELECT 1 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a
            WHERE a.grantee = r.oid)
        AND NOT EXISTS (
            SELECT 1 FROM (
                SELECT datacl AS acl FROM pg_database
                UNION ALL SELECT nspacl FROM pg_namespace
                UNION ALL SELECT relacl FROM pg_class
                UNION ALL SELECT attacl FROM pg_attribute
                UNION ALL SELECT proacl FROM pg_proc
                UNION ALL SELECT typacl FROM pg_type
                UNION ALL SELECT lanacl FROM pg_language
                UNION ALL SELECT spcacl FROM pg_tablespace
                UNION ALL SELECT fdwacl FROM pg_foreign_data_wrapper
                UNION ALL SELECT srvacl FROM pg_foreign_server
                UNION ALL SELECT lomacl FROM pg_largeobject_metadata
                UNION ALL SELECT paracl FROM pg_parameter_acl
            ) object CROSS JOIN LATERAL aclexplode(object.acl) a
            WHERE a.grantee = r.oid AND a.is_grantable)
    ) AS safe FROM restricted r
)
SELECT EXISTS (SELECT 1 FROM authority a WHERE a.safe
    AND has_database_privilege(a.oid, current_database(), 'CONNECT')
    AND has_schema_privilege(a.oid, 'public', 'USAGE')
    AND (CASE current_setting('mk8.restricted_role_kind', true)
        WHEN 'gateway' THEN NOT EXISTS (
            SELECT 1 FROM gateway_tables g LEFT JOIN pg_class c ON c.oid = to_regclass('public.' || g.table_name)
            WHERE c.oid IS NULL OR NOT has_table_privilege(a.oid, c.oid, g.privilege))
            AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'mk8_restore_state'
                    AND NOT has_column_privilege(a.oid, c.oid, 'state', 'SELECT'))
        WHEN 'wake' THEN NOT EXISTS (
            SELECT 1 FROM wake_columns w LEFT JOIN pg_class c ON c.oid = to_regclass('public.' || w.table_name)
            LEFT JOIN pg_attribute attribute ON attribute.attrelid = c.oid AND attribute.attname = w.column_name
                AND attribute.attnum > 0 AND NOT attribute.attisdropped
            WHERE c.oid IS NULL OR attribute.attnum IS NULL OR NOT has_column_privilege(a.oid, c.oid, w.column_name, 'SELECT'))
        ELSE false END)) AS restricted_role_safe,
    EXISTS (SELECT 1 FROM authority WHERE safe) AS restricted_role_authority_safe
\gset
