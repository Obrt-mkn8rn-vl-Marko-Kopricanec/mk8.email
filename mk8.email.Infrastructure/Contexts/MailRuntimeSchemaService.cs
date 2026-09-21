using System.Data;
using Microsoft.EntityFrameworkCore;

namespace mk8.email.Infrastructure.Data;

public sealed class MailRuntimeSchemaService(EmailDbContext database)
{
    private static readonly IReadOnlyDictionary<string, string> RequiredMessageColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["envelope_sender"] = "varchar",
            ["raw_message"] = "text",
            ["requires_smtp_utf8"] = "bool",
            ["dsn_return_content"] = "varchar",
            ["dsn_envelope_id"] = "varchar",
            ["client_ip"] = "varchar",
            ["helo"] = "varchar",
            ["authenticated_user"] = "varchar",
            ["direction"] = "varchar",
            ["state"] = "varchar",
            ["scan_state"] = "varchar",
            ["scan_action"] = "varchar",
            ["scan_score"] = "float8",
            ["added_headers"] = "text",
            ["target_folder"] = "varchar",
            ["attempt_count"] = "int4",
            ["received_at"] = "timestamptz",
            ["next_attempt_at"] = "timestamptz",
            ["lease_token"] = "uuid",
            ["lease_expires_at"] = "timestamptz",
            ["last_error"] = "text",
            ["sent_copy_created"] = "bool",
            ["completed_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredRecipientColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["message_id"] = "uuid",
            ["recipient"] = "varchar",
            ["is_local"] = "bool",
            ["state"] = "varchar",
            ["attempt_count"] = "int4",
            ["next_attempt_at"] = "timestamptz",
            ["last_attempt_at"] = "timestamptz",
            ["last_error"] = "text",
            ["last_enhanced_status_code"] = "varchar",
            ["last_remote_mta"] = "varchar",
            ["failure_notice_created"] = "bool",
            ["success_notice_created"] = "bool",
            ["delay_notice_created"] = "bool",
            ["dsn_notify"] = "varchar",
            ["dsn_original_recipient"] = "varchar",
            ["dsn_forwarded"] = "bool",
            ["redirect_depth"] = "int4",
            ["redirect_history"] = "_text",
            ["completed_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredSieveScriptColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["name"] = "varchar",
            ["content"] = "text",
            ["is_active"] = "bool",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredEmailColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["queue_delivery_id"] = "uuid",
            ["keywords"] = "_text",
            ["raw_message"] = "bytea",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredFolderColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jmap_role"] = "varchar",
            ["suppress_default_jmap_role"] = "bool",
            ["sort_order"] = "int8",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapChangeColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sequence"] = "int8",
            ["account_id"] = "uuid",
            ["data_type"] = "varchar",
            ["object_id"] = "varchar",
            ["change_kind"] = "varchar",
            ["changed_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapBlobColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["blob_id"] = "varchar",
            ["account_id"] = "uuid",
            ["content_type"] = "varchar",
            ["name"] = "varchar",
            ["content"] = "bytea",
            ["size_bytes"] = "int8",
            ["created_at"] = "timestamptz",
            ["expires_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapSubmissionColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["submission_object_id"] = "varchar",
            ["account_id"] = "uuid",
            ["identity_id"] = "varchar",
            ["email_id"] = "varchar",
            ["thread_id"] = "varchar",
            ["queue_id"] = "uuid",
            ["envelope_sender"] = "varchar",
            ["envelope_recipients"] = "_text",
            ["envelope_json"] = "jsonb",
            ["undo_status"] = "varchar",
            ["send_at"] = "timestamptz",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapPushColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["subscription_object_id"] = "varchar",
            ["user_id"] = "uuid",
            ["device_client_id"] = "varchar",
            ["url"] = "varchar",
            ["types"] = "_text",
            ["keys_json"] = "jsonb",
            ["verification_code"] = "varchar",
            ["is_verified"] = "bool",
            ["expires_at"] = "timestamptz",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
            ["last_pushed_change"] = "int8",
            ["next_push_at"] = "timestamptz",
            ["failure_count"] = "int4",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapVacationColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["account_id"] = "uuid",
            ["is_enabled"] = "bool",
            ["from_date"] = "timestamptz",
            ["to_date"] = "timestamptz",
            ["subject"] = "varchar",
            ["text_body"] = "text",
            ["html_body"] = "text",
            ["updated_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapVacationReplyColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["account_id"] = "uuid",
            ["sender_address"] = "varchar",
            ["last_delivery_id"] = "uuid",
            ["last_sent_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredJmapIdentityColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["identity_object_id"] = "varchar",
            ["account_id"] = "uuid",
            ["name"] = "varchar",
            ["email"] = "varchar",
            ["reply_to_json"] = "jsonb",
            ["bcc_json"] = "jsonb",
            ["text_signature"] = "text",
            ["html_signature"] = "text",
            ["may_delete"] = "bool",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredDavCollectionColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["collection_type"] = "varchar",
            ["slug"] = "varchar",
            ["display_name"] = "varchar",
            ["description"] = "varchar",
            ["color"] = "varchar",
            ["sort_order"] = "int4",
            ["components"] = "_text",
            ["sync_token"] = "int8",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredDavResourceColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["collection_id"] = "uuid",
            ["resource_name"] = "varchar",
            ["uid"] = "varchar",
            ["content_type"] = "varchar",
            ["content"] = "bytea",
            ["etag"] = "varchar",
            ["size_bytes"] = "int4",
            ["change_sequence"] = "int8",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredDavChangeColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["collection_id"] = "uuid",
            ["sequence"] = "int8",
            ["resource_name"] = "varchar",
            ["is_deleted"] = "bool",
            ["etag"] = "varchar",
            ["changed_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredDavShareColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["collection_id"] = "uuid",
            ["grantee_user_id"] = "uuid",
            ["access_level"] = "varchar",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                database.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return;
        }

        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS mail_queue_messages (
                id uuid PRIMARY KEY,
                envelope_sender varchar(320) NOT NULL,
                raw_message text NOT NULL,
                requires_smtp_utf8 boolean NOT NULL DEFAULT false,
                dsn_return_content varchar(4),
                dsn_envelope_id varchar(100),
                client_ip varchar(45),
                helo varchar(255),
                authenticated_user varchar(320),
                direction varchar(16) NOT NULL,
                state varchar(24) NOT NULL,
                scan_state varchar(24) NOT NULL,
                scan_action varchar(32),
                scan_score double precision,
                added_headers text,
                target_folder varchar(32),
                attempt_count integer NOT NULL DEFAULT 0,
                received_at timestamp with time zone NOT NULL,
                next_attempt_at timestamp with time zone NOT NULL,
                lease_token uuid,
                lease_expires_at timestamp with time zone,
                last_error text,
                sent_copy_created boolean NOT NULL DEFAULT false,
                completed_at timestamp with time zone,
                CONSTRAINT ck_mail_queue_messages_direction
                    CHECK (direction IN ('inbound', 'submission')),
                CONSTRAINT ck_mail_queue_messages_state
                    CHECK (state IN ('pending', 'processing', 'completed', 'quarantined', 'dead')),
                CONSTRAINT ck_mail_queue_messages_scan_state
                    CHECK (scan_state IN ('pending', 'complete')),
                CONSTRAINT ck_mail_queue_messages_attempt_count
                    CHECK (attempt_count >= 0)
            );

            CREATE TABLE IF NOT EXISTS mail_queue_recipients (
                id uuid PRIMARY KEY,
                message_id uuid NOT NULL REFERENCES mail_queue_messages(id) ON DELETE CASCADE,
                recipient varchar(320) NOT NULL,
                is_local boolean NOT NULL,
                state varchar(24) NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                next_attempt_at timestamp with time zone NOT NULL,
                last_attempt_at timestamp with time zone,
                last_error text,
                last_enhanced_status_code varchar(16),
                last_remote_mta varchar(255),
                failure_notice_created boolean NOT NULL DEFAULT false,
                success_notice_created boolean NOT NULL DEFAULT false,
                delay_notice_created boolean NOT NULL DEFAULT false,
                dsn_notify varchar(28),
                dsn_original_recipient varchar(500),
                dsn_forwarded boolean NOT NULL DEFAULT false,
                redirect_depth integer NOT NULL DEFAULT 0,
                redirect_history text[] NOT NULL DEFAULT ARRAY[]::text[],
                completed_at timestamp with time zone,
                CONSTRAINT ck_mail_queue_recipients_state
                    CHECK (state IN ('pending', 'delivered', 'permanent_failure', 'quarantined')),
                CONSTRAINT ck_mail_queue_recipients_attempt_count
                    CHECK (attempt_count >= 0),
                CONSTRAINT ck_mail_queue_recipients_redirect_depth
                    CHECK (redirect_depth >= 0)
            );

            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS redirect_depth integer NOT NULL DEFAULT 0;
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS redirect_history text[] NOT NULL DEFAULT ARRAY[]::text[];
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS requires_smtp_utf8 boolean NOT NULL DEFAULT false;
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS dsn_return_content varchar(4);
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS dsn_envelope_id varchar(100);
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS success_notice_created boolean NOT NULL DEFAULT false;
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS delay_notice_created boolean NOT NULL DEFAULT false;
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS dsn_notify varchar(28);
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS dsn_original_recipient varchar(500);
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS dsn_forwarded boolean NOT NULL DEFAULT false;
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS last_enhanced_status_code varchar(16);
            ALTER TABLE mail_queue_recipients
                ADD COLUMN IF NOT EXISTS last_remote_mta varchar(255);

            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS queue_delivery_id uuid;
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS keywords text[] NOT NULL DEFAULT ARRAY[]::text[];
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS raw_message bytea;
            ALTER TABLE folders
                ADD COLUMN IF NOT EXISTS jmap_role varchar(32);
            ALTER TABLE folders
                ADD COLUMN IF NOT EXISTS suppress_default_jmap_role boolean NOT NULL DEFAULT false;
            ALTER TABLE folders
                ADD COLUMN IF NOT EXISTS sort_order bigint NOT NULL DEFAULT 0;
            ALTER TABLE folders
                ALTER COLUMN sort_order TYPE bigint;
            ALTER TABLE folders
                ALTER COLUMN name TYPE varchar(5049);

            UPDATE folders
            SET jmap_role = CASE lower(name)
                WHEN 'inbox' THEN 'inbox'
                WHEN 'sent' THEN 'sent'
                WHEN 'drafts' THEN 'drafts'
                WHEN 'trash' THEN 'trash'
                WHEN 'spam' THEN 'junk'
                ELSE jmap_role
            END
            WHERE jmap_role IS NULL
              AND NOT suppress_default_jmap_role
              AND lower(name) IN ('inbox', 'sent', 'drafts', 'trash', 'spam');

            CREATE TABLE IF NOT EXISTS jmap_changes (
                sequence bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                account_id uuid NOT NULL,
                data_type varchar(32) NOT NULL,
                object_id varchar(255) NOT NULL,
                change_kind varchar(16) NOT NULL,
                changed_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_jmap_changes_kind
                    CHECK (change_kind IN ('created', 'updated', 'destroyed'))
            );

            CREATE TABLE IF NOT EXISTS jmap_blobs (
                id uuid PRIMARY KEY,
                blob_id varchar(64) NOT NULL,
                account_id uuid NOT NULL REFERENCES inboxes(id) ON DELETE CASCADE,
                content_type varchar(255) NOT NULL,
                name varchar(255),
                content bytea NOT NULL,
                size_bytes bigint NOT NULL,
                created_at timestamp with time zone NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_jmap_blobs_size CHECK (size_bytes >= 0)
            );

            CREATE TABLE IF NOT EXISTS jmap_email_submissions (
                id uuid PRIMARY KEY,
                submission_object_id varchar(64) NOT NULL,
                account_id uuid NOT NULL REFERENCES inboxes(id) ON DELETE CASCADE,
                identity_id varchar(64) NOT NULL,
                email_id varchar(64) NOT NULL,
                thread_id varchar(64) NOT NULL,
                queue_id uuid NOT NULL,
                envelope_sender varchar(320) NOT NULL,
                envelope_recipients text[] NOT NULL,
                envelope_json jsonb,
                undo_status varchar(16) NOT NULL,
                send_at timestamp with time zone NOT NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL
            );

            ALTER TABLE jmap_email_submissions
                ADD COLUMN IF NOT EXISTS envelope_json jsonb;

            CREATE TABLE IF NOT EXISTS jmap_push_subscriptions (
                id uuid PRIMARY KEY,
                subscription_object_id varchar(64) NOT NULL,
                user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                device_client_id varchar(255) NOT NULL,
                url varchar(2048) NOT NULL,
                types text[],
                keys_json jsonb,
                verification_code varchar(128) NOT NULL,
                is_verified boolean NOT NULL DEFAULT false,
                expires_at timestamp with time zone NOT NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                last_pushed_change bigint NOT NULL DEFAULT 0,
                next_push_at timestamp with time zone,
                failure_count integer NOT NULL DEFAULT 0
            );

            ALTER TABLE jmap_push_subscriptions
                ADD COLUMN IF NOT EXISTS next_push_at timestamp with time zone;
            ALTER TABLE jmap_push_subscriptions
                ADD COLUMN IF NOT EXISTS failure_count integer NOT NULL DEFAULT 0;

            CREATE TABLE IF NOT EXISTS jmap_vacation_responses (
                account_id uuid PRIMARY KEY REFERENCES inboxes(id) ON DELETE CASCADE,
                is_enabled boolean NOT NULL DEFAULT false,
                from_date timestamp with time zone,
                to_date timestamp with time zone,
                subject varchar(998),
                text_body text,
                html_body text,
                updated_at timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS jmap_vacation_replies (
                id uuid PRIMARY KEY,
                account_id uuid NOT NULL REFERENCES inboxes(id) ON DELETE CASCADE,
                sender_address varchar(320) NOT NULL,
                last_delivery_id uuid NOT NULL,
                last_sent_at timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS jmap_identities (
                id uuid PRIMARY KEY,
                identity_object_id varchar(64) NOT NULL,
                account_id uuid NOT NULL REFERENCES inboxes(id) ON DELETE CASCADE,
                name varchar(255) NOT NULL,
                email varchar(320) NOT NULL,
                reply_to_json jsonb,
                bcc_json jsonb,
                text_signature text NOT NULL,
                html_signature text NOT NULL,
                may_delete boolean NOT NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS dav_collections (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                collection_type varchar(16) NOT NULL,
                slug varchar(128) NOT NULL,
                display_name varchar(255) NOT NULL,
                description varchar(1024),
                color varchar(32),
                sort_order integer NOT NULL DEFAULT 0,
                components text[] NOT NULL DEFAULT ARRAY[]::text[],
                sync_token bigint NOT NULL DEFAULT 0,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_dav_collections_type
                    CHECK (collection_type IN ('calendar', 'addressbook')),
                CONSTRAINT ck_dav_collections_sync_token
                    CHECK (sync_token >= 0)
            );

            CREATE TABLE IF NOT EXISTS dav_resources (
                id uuid PRIMARY KEY,
                collection_id uuid NOT NULL REFERENCES dav_collections(id) ON DELETE CASCADE,
                resource_name varchar(255) NOT NULL,
                uid varchar(255) NOT NULL,
                content_type varchar(255) NOT NULL,
                content bytea NOT NULL,
                etag varchar(64) NOT NULL,
                size_bytes integer NOT NULL,
                change_sequence bigint NOT NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_dav_resources_size CHECK (size_bytes >= 0),
                CONSTRAINT ck_dav_resources_change_sequence CHECK (change_sequence > 0)
            );

            CREATE TABLE IF NOT EXISTS dav_changes (
                id uuid PRIMARY KEY,
                collection_id uuid NOT NULL REFERENCES dav_collections(id) ON DELETE CASCADE,
                sequence bigint NOT NULL,
                resource_name varchar(255) NOT NULL,
                is_deleted boolean NOT NULL,
                etag varchar(64),
                changed_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_dav_changes_sequence CHECK (sequence > 0)
            );

            CREATE TABLE IF NOT EXISTS dav_shares (
                id uuid PRIMARY KEY,
                collection_id uuid NOT NULL REFERENCES dav_collections(id) ON DELETE CASCADE,
                grantee_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                access_level varchar(16) NOT NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_dav_shares_access
                    CHECK (access_level IN ('read', 'read-write'))
            );

            CREATE TABLE IF NOT EXISTS sieve_scripts (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                name varchar(512) NOT NULL,
                content text NOT NULL,
                is_active boolean NOT NULL DEFAULT false,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_emails_queue_delivery_id
                ON emails (queue_delivery_id)
                WHERE queue_delivery_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_folders_inbox_id_mailbox_id
                ON folders (inbox_id, mailbox_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_folders_inbox_id_jmap_role
                ON folders (inbox_id, jmap_role)
                WHERE jmap_role IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_mail_queue_messages_state_next_attempt_at
                ON mail_queue_messages (state, next_attempt_at);
            CREATE INDEX IF NOT EXISTS ix_mail_queue_messages_received_at
                ON mail_queue_messages (received_at);
            CREATE INDEX IF NOT EXISTS ix_mail_queue_recipients_message_id_state
                ON mail_queue_recipients (message_id, state);
            CREATE INDEX IF NOT EXISTS ix_jmap_changes_account_type_sequence
                ON jmap_changes (account_id, data_type, sequence);
            CREATE INDEX IF NOT EXISTS ix_jmap_changes_changed_at
                ON jmap_changes (changed_at);
            DELETE FROM jmap_changes duplicate
            USING jmap_changes original
            WHERE duplicate.data_type = '_Account'
              AND original.data_type = '_Account'
              AND duplicate.account_id = original.account_id
              AND duplicate.sequence > original.sequence;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_changes_account_baseline
                ON jmap_changes (account_id)
                WHERE data_type = '_Account';
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_blobs_blob_id
                ON jmap_blobs (blob_id);
            CREATE INDEX IF NOT EXISTS ix_jmap_blobs_account_expires
                ON jmap_blobs (account_id, expires_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_email_submissions_object_id
                ON jmap_email_submissions (submission_object_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_email_submissions_queue_id
                ON jmap_email_submissions (queue_id);
            CREATE INDEX IF NOT EXISTS ix_jmap_email_submissions_account_created
                ON jmap_email_submissions (account_id, created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_push_subscriptions_object_id
                ON jmap_push_subscriptions (subscription_object_id);
            DROP INDEX IF EXISTS ix_jmap_push_subscriptions_user_device;
            CREATE INDEX ix_jmap_push_subscriptions_user_device
                ON jmap_push_subscriptions (user_id, device_client_id);
            CREATE INDEX IF NOT EXISTS ix_jmap_push_subscriptions_expires_at
                ON jmap_push_subscriptions (expires_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_vacation_replies_account_sender
                ON jmap_vacation_replies (account_id, sender_address);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_vacation_replies_delivery_id
                ON jmap_vacation_replies (last_delivery_id);
            CREATE INDEX IF NOT EXISTS ix_jmap_vacation_replies_last_sent_at
                ON jmap_vacation_replies (last_sent_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_jmap_identities_object_id
                ON jmap_identities (identity_object_id);
            CREATE INDEX IF NOT EXISTS ix_jmap_identities_account_email
                ON jmap_identities (account_id, email);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_collections_user_type_slug
                ON dav_collections (user_id, collection_type, slug);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_resources_collection_name
                ON dav_resources (collection_id, resource_name);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_resources_collection_uid
                ON dav_resources (collection_id, uid);
            CREATE INDEX IF NOT EXISTS ix_dav_resources_collection_sequence
                ON dav_resources (collection_id, change_sequence);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_changes_collection_sequence
                ON dav_changes (collection_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_dav_changes_changed_at
                ON dav_changes (changed_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_shares_collection_grantee
                ON dav_shares (collection_id, grantee_user_id);
            CREATE INDEX IF NOT EXISTS ix_dav_shares_grantee
                ON dav_shares (grantee_user_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sieve_scripts_user_name
                ON sieve_scripts (user_id, name);
            ALTER TABLE sieve_scripts
                ALTER COLUMN name TYPE varchar(512);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sieve_scripts_user_active
                ON sieve_scripts (user_id)
                WHERE is_active;
            """,
            cancellationToken);

        await ValidateTableAsync(
            "mail_queue_messages",
            RequiredMessageColumns,
            cancellationToken);
        await ValidateTableAsync(
            "mail_queue_recipients",
            RequiredRecipientColumns,
            cancellationToken);
        await ValidateTableAsync(
            "emails",
            RequiredEmailColumns,
            cancellationToken);
        await ValidateTableAsync(
            "folders",
            RequiredFolderColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_changes",
            RequiredJmapChangeColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_blobs",
            RequiredJmapBlobColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_email_submissions",
            RequiredJmapSubmissionColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_push_subscriptions",
            RequiredJmapPushColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_vacation_responses",
            RequiredJmapVacationColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_vacation_replies",
            RequiredJmapVacationReplyColumns,
            cancellationToken);
        await ValidateTableAsync(
            "jmap_identities",
            RequiredJmapIdentityColumns,
            cancellationToken);
        await ValidateTableAsync(
            "dav_collections",
            RequiredDavCollectionColumns,
            cancellationToken);
        await ValidateTableAsync(
            "dav_resources",
            RequiredDavResourceColumns,
            cancellationToken);
        await ValidateTableAsync(
            "dav_changes",
            RequiredDavChangeColumns,
            cancellationToken);
        await ValidateTableAsync(
            "dav_shares",
            RequiredDavShareColumns,
            cancellationToken);
        await ValidateTableAsync(
            "sieve_scripts",
            RequiredSieveScriptColumns,
            cancellationToken);
    }

    private async Task ValidateTableAsync(
        string tableName,
        IReadOnlyDictionary<string, string> requiredColumns,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, udt_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table_name
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table_name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var actualColumns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actualColumns.Add(reader.GetString(0), reader.GetString(1));

        foreach (var requiredColumn in requiredColumns)
        {
            if (!actualColumns.TryGetValue(requiredColumn.Key, out var actualType)
                || !string.Equals(actualType, requiredColumn.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The {tableName}.{requiredColumn.Key} database column is missing or invalid.");
            }
        }
    }

}
