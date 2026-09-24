using System.Data;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Data;

public sealed class MailRuntimeSchemaService(EmailDbContext database)
{
    private static readonly Dictionary<string, string> RequiredMessageColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["envelope_sender"] = "varchar",
            ["raw_message"] = "text",
            ["raw_message_size_bytes"] = "int8",
            ["raw_message_object_provider"] = "varchar",
            ["raw_message_object_name"] = "varchar",
            ["raw_message_object_sha256"] = "varchar",
            ["raw_message_object_etag"] = "varchar",
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

    private static readonly Dictionary<string, string> RequiredRecipientColumns =
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

    private static readonly Dictionary<string, string> RequiredSieveScriptColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["name"] = "varchar",
            ["content"] = "text",
            ["size_bytes"] = "int4",
            ["object_provider"] = "varchar",
            ["object_name"] = "varchar",
            ["object_sha256"] = "varchar",
            ["object_etag"] = "varchar",
            ["is_active"] = "bool",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredEmailColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["queue_delivery_id"] = "uuid",
            ["keywords"] = "_text",
            ["raw_message"] = "bytea",
            ["raw_message_object_provider"] = "varchar",
            ["raw_message_object_name"] = "varchar",
            ["raw_message_object_sha256"] = "varchar",
            ["raw_message_object_etag"] = "varchar",
            ["email_object_id"] = "varchar",
            ["thread_object_id"] = "varchar",
        };

    private static readonly Dictionary<string, string> RequiredFolderColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jmap_role"] = "varchar",
            ["suppress_default_jmap_role"] = "bool",
            ["sort_order"] = "int8",
            ["mailbox_id"] = "varchar",
        };

    private static readonly Dictionary<string, string> RequiredJmapChangeColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sequence"] = "int8",
            ["account_id"] = "uuid",
            ["data_type"] = "varchar",
            ["object_id"] = "varchar",
            ["change_kind"] = "varchar",
            ["changed_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredJmapBlobColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["blob_id"] = "varchar",
            ["account_id"] = "uuid",
            ["content_type"] = "varchar",
            ["name"] = "varchar",
            ["content"] = "bytea",
            ["object_provider"] = "varchar",
            ["object_name"] = "varchar",
            ["object_sha256"] = "varchar",
            ["object_etag"] = "varchar",
            ["size_bytes"] = "int8",
            ["created_at"] = "timestamptz",
            ["expires_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredJmapSubmissionColumns =
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

    private static readonly Dictionary<string, string> RequiredJmapPushColumns =
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

    private static readonly Dictionary<string, string> RequiredJmapVacationColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["account_id"] = "uuid",
            ["is_enabled"] = "bool",
            ["from_date"] = "timestamptz",
            ["to_date"] = "timestamptz",
            ["subject"] = "varchar",
            ["text_body"] = "text",
            ["html_body"] = "text",
            ["body_size_bytes"] = "int4",
            ["body_object_provider"] = "varchar",
            ["body_object_name"] = "varchar",
            ["body_object_sha256"] = "varchar",
            ["body_object_etag"] = "varchar",
            ["updated_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredJmapVacationReplyColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["account_id"] = "uuid",
            ["sender_address"] = "varchar",
            ["last_delivery_id"] = "uuid",
            ["last_sent_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredJmapIdentityColumns =
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

    private static readonly Dictionary<string, string> RequiredDavCollectionColumns =
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
            ["is_default"] = "bool",
            ["is_subscribed"] = "bool",
            ["components"] = "_text",
            ["sync_token"] = "int8",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredDavResourceColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["collection_id"] = "uuid",
            ["addressbook_user_id"] = "uuid",
            ["resource_name"] = "varchar",
            ["uid"] = "varchar",
            ["content_type"] = "varchar",
            ["content"] = "bytea",
            ["object_provider"] = "varchar",
            ["object_name"] = "varchar",
            ["object_sha256"] = "varchar",
            ["object_etag"] = "varchar",
            ["etag"] = "varchar",
            ["size_bytes"] = "int4",
            ["change_sequence"] = "int8",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredDavChangeColumns =
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

    private static readonly Dictionary<string, string> RequiredDavShareColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["collection_id"] = "uuid",
            ["grantee_user_id"] = "uuid",
            ["access_level"] = "varchar",
            ["created_at"] = "timestamptz",
            ["updated_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredApplicationPasswordColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["name"] = "varchar",
            ["password_hash"] = "varchar",
            ["created_at"] = "timestamptz",
            ["last_used_at"] = "timestamptz",
            ["revoked_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredOAuthGrantColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["client_id"] = "varchar",
            ["device_name"] = "varchar",
            ["scopes"] = "_text",
            ["created_at"] = "timestamptz",
            ["last_used_at"] = "timestamptz",
            ["revoked_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredOAuthTokenColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["grant_id"] = "uuid",
            ["token_type"] = "varchar",
            ["token_hash"] = "bytea",
            ["created_at"] = "timestamptz",
            ["expires_at"] = "timestamptz",
            ["last_used_at"] = "timestamptz",
            ["revoked_at"] = "timestamptz",
            ["replaced_by_token_id"] = "uuid",
        };

    private static readonly Dictionary<string, string> RequiredOAuthAuthorizationCodeColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["client_id"] = "varchar",
            ["redirect_uri"] = "varchar",
            ["device_name"] = "varchar",
            ["scopes"] = "_text",
            ["code_challenge"] = "varchar",
            ["code_hash"] = "bytea",
            ["nonce"] = "varchar",
            ["created_at"] = "timestamptz",
            ["expires_at"] = "timestamptz",
            ["consumed_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredMfaTotpCredentialColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["user_id"] = "uuid",
            ["name"] = "varchar",
            ["encrypted_secret"] = "bytea",
            ["encryption_nonce"] = "bytea",
            ["encryption_tag"] = "bytea",
            ["created_at"] = "timestamptz",
            ["verified_at"] = "timestamptz",
            ["last_used_at"] = "timestamptz",
            ["last_accepted_time_step"] = "int8",
            ["revoked_at"] = "timestamptz",
        };

    private static readonly Dictionary<string, string> RequiredMfaRecoveryCodeColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "uuid",
            ["credential_id"] = "uuid",
            ["code_hash"] = "bytea",
            ["created_at"] = "timestamptz",
            ["used_at"] = "timestamptz",
        };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ordered relational schema upgrade is an existing atomic migration sequence.")]
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
                raw_message text,
                raw_message_size_bytes bigint NOT NULL,
                raw_message_object_provider varchar(32),
                raw_message_object_name varchar(1024),
                raw_message_object_sha256 varchar(64),
                raw_message_object_etag varchar(256),
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
                    CHECK (attempt_count >= 0),
                CONSTRAINT ck_mail_queue_messages_raw_storage_shape CHECK (
                    raw_message_size_bytes >= 0
                    AND ((raw_message IS NOT NULL
                            AND raw_message_object_provider IS NULL
                            AND raw_message_object_name IS NULL
                            AND raw_message_object_sha256 IS NULL
                            AND raw_message_object_etag IS NULL)
                        OR
                        (raw_message IS NULL
                            AND raw_message_object_provider = 'azure-blob'
                            AND raw_message_object_name IS NOT NULL
                            AND raw_message_object_sha256 IS NOT NULL
                            AND raw_message_object_etag IS NOT NULL)))
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
            ALTER TABLE mail_queue_messages
                ALTER COLUMN raw_message DROP NOT NULL;
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS raw_message_size_bytes bigint;
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS raw_message_object_provider varchar(32);
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS raw_message_object_name varchar(1024);
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS raw_message_object_sha256 varchar(64);
            ALTER TABLE mail_queue_messages
                ADD COLUMN IF NOT EXISTS raw_message_object_etag varchar(256);
            UPDATE mail_queue_messages
            SET raw_message_size_bytes = char_length(raw_message)
            WHERE raw_message_size_bytes IS NULL AND raw_message IS NOT NULL;
            ALTER TABLE mail_queue_messages
                ALTER COLUMN raw_message_size_bytes SET NOT NULL;
            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'mail_queue_messages'::regclass
                      AND conname = 'ck_mail_queue_messages_raw_storage_shape'
                ) THEN
                    ALTER TABLE mail_queue_messages
                        ADD CONSTRAINT ck_mail_queue_messages_raw_storage_shape CHECK (
                            raw_message_size_bytes >= 0
                            AND ((raw_message IS NOT NULL
                                    AND raw_message_object_provider IS NULL
                                    AND raw_message_object_name IS NULL
                                    AND raw_message_object_sha256 IS NULL
                                    AND raw_message_object_etag IS NULL)
                                OR
                                (raw_message IS NULL
                                    AND raw_message_object_provider = 'azure-blob'
                                    AND raw_message_object_name IS NOT NULL
                                    AND raw_message_object_sha256 IS NOT NULL
                                    AND raw_message_object_etag IS NOT NULL))) NOT VALID;
                END IF;
            END
            $migration$;
            ALTER TABLE mail_queue_messages
                VALIDATE CONSTRAINT ck_mail_queue_messages_raw_storage_shape;
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
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS raw_message_object_provider varchar(32);
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS raw_message_object_name varchar(1024);
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS raw_message_object_sha256 varchar(64);
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS raw_message_object_etag varchar(256);
            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'emails'::regclass
                      AND conname = 'ck_emails_raw_storage_shape'
                ) THEN
                    ALTER TABLE emails
                        ADD CONSTRAINT ck_emails_raw_storage_shape CHECK (
                            (raw_message IS NOT NULL
                                AND raw_message_object_provider IS NULL
                                AND raw_message_object_name IS NULL
                                AND raw_message_object_sha256 IS NULL
                                AND raw_message_object_etag IS NULL)
                            OR
                            (raw_message IS NULL
                                AND raw_message_object_provider = 'azure-blob'
                                AND raw_message_object_name IS NOT NULL
                                AND raw_message_object_sha256 IS NOT NULL
                                AND raw_message_object_etag IS NOT NULL)
                            OR
                            (raw_message IS NULL
                                AND raw_message_object_provider IS NULL
                                AND raw_message_object_name IS NULL
                                AND raw_message_object_sha256 IS NULL
                                AND raw_message_object_etag IS NULL)) NOT VALID;
                END IF;
            END
            $migration$;
            ALTER TABLE emails
                VALIDATE CONSTRAINT ck_emails_raw_storage_shape;
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS email_object_id varchar(64);
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS thread_object_id varchar(64);
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
            ALTER TABLE folders
                ADD COLUMN IF NOT EXISTS mailbox_id varchar(64);

            UPDATE folders
            SET mailbox_id = replace(id::text, '-', '')
            WHERE mailbox_id IS NULL OR mailbox_id = '';

            WITH duplicate_mailbox_ids AS (
                SELECT id
                FROM (
                    SELECT id,
                           row_number() OVER (PARTITION BY mailbox_id ORDER BY id) AS duplicate_number
                    FROM folders
                ) ranked
                WHERE duplicate_number > 1
            )
            UPDATE folders
            SET mailbox_id = replace(folders.id::text, '-', '')
            FROM duplicate_mailbox_ids
            WHERE folders.id = duplicate_mailbox_ids.id;

            UPDATE emails
            SET email_object_id = replace(id::text, '-', '')
            WHERE email_object_id IS NULL OR email_object_id = '';

            UPDATE emails
            SET thread_object_id = replace(id::text, '-', '')
            WHERE thread_object_id IS NULL OR thread_object_id = '';

            ALTER TABLE folders
                ALTER COLUMN mailbox_id SET NOT NULL;
            ALTER TABLE emails
                ALTER COLUMN email_object_id SET NOT NULL;

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
                content bytea,
                object_provider varchar(32),
                object_name varchar(1024),
                object_sha256 varchar(64),
                object_etag varchar(256),
                size_bytes bigint NOT NULL,
                created_at timestamp with time zone NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_jmap_blobs_size CHECK (size_bytes >= 0),
                CONSTRAINT ck_jmap_blobs_storage_shape CHECK (
                    (content IS NOT NULL
                        AND object_provider IS NULL
                        AND object_name IS NULL
                        AND object_sha256 IS NULL
                        AND object_etag IS NULL)
                    OR
                    (content IS NULL
                        AND object_provider = 'azure-blob'
                        AND object_name IS NOT NULL
                        AND object_sha256 IS NOT NULL
                        AND object_etag IS NOT NULL))
            );

            ALTER TABLE jmap_blobs
                ALTER COLUMN content DROP NOT NULL;
            ALTER TABLE jmap_blobs
                ADD COLUMN IF NOT EXISTS object_provider varchar(32);
            ALTER TABLE jmap_blobs
                ADD COLUMN IF NOT EXISTS object_name varchar(1024);
            ALTER TABLE jmap_blobs
                ADD COLUMN IF NOT EXISTS object_sha256 varchar(64);
            ALTER TABLE jmap_blobs
                ADD COLUMN IF NOT EXISTS object_etag varchar(256);
            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'jmap_blobs'::regclass
                      AND conname = 'ck_jmap_blobs_storage_shape'
                ) THEN
                    ALTER TABLE jmap_blobs
                        ADD CONSTRAINT ck_jmap_blobs_storage_shape CHECK (
                            (content IS NOT NULL
                                AND object_provider IS NULL
                                AND object_name IS NULL
                                AND object_sha256 IS NULL
                                AND object_etag IS NULL)
                            OR
                            (content IS NULL
                                AND object_provider = 'azure-blob'
                                AND object_name IS NOT NULL
                                AND object_sha256 IS NOT NULL
                                AND object_etag IS NOT NULL)) NOT VALID;
                END IF;
            END
            $migration$;
            ALTER TABLE jmap_blobs
                VALIDATE CONSTRAINT ck_jmap_blobs_storage_shape;

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
                body_size_bytes integer NOT NULL DEFAULT 0,
                body_object_provider varchar(32),
                body_object_name varchar(1024),
                body_object_sha256 varchar(64),
                body_object_etag varchar(256),
                updated_at timestamp with time zone NOT NULL
            );

            ALTER TABLE jmap_vacation_responses
                ADD COLUMN IF NOT EXISTS body_size_bytes integer NOT NULL DEFAULT 0;
            ALTER TABLE jmap_vacation_responses
                ADD COLUMN IF NOT EXISTS body_object_provider varchar(32);
            ALTER TABLE jmap_vacation_responses
                ADD COLUMN IF NOT EXISTS body_object_name varchar(1024);
            ALTER TABLE jmap_vacation_responses
                ADD COLUMN IF NOT EXISTS body_object_sha256 varchar(64);
            ALTER TABLE jmap_vacation_responses
                ADD COLUMN IF NOT EXISTS body_object_etag varchar(256);

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
                is_default boolean NOT NULL DEFAULT false,
                is_subscribed boolean NOT NULL DEFAULT true,
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
                addressbook_user_id uuid,
                resource_name varchar(255) NOT NULL,
                uid varchar(255) NOT NULL,
                content_type varchar(255) NOT NULL,
                content bytea,
                object_provider varchar(32),
                object_name varchar(1024),
                object_sha256 varchar(64),
                object_etag varchar(256),
                etag varchar(64) NOT NULL,
                size_bytes integer NOT NULL,
                change_sequence bigint NOT NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_dav_resources_size CHECK (size_bytes >= 0),
                CONSTRAINT ck_dav_resources_change_sequence CHECK (change_sequence > 0),
                CONSTRAINT ck_dav_resources_storage_shape CHECK (
                    (content IS NOT NULL
                        AND object_provider IS NULL
                        AND object_name IS NULL
                        AND object_sha256 IS NULL
                        AND object_etag IS NULL)
                    OR
                    (content IS NULL
                        AND object_provider = 'azure-blob'
                        AND object_name IS NOT NULL
                        AND object_sha256 IS NOT NULL
                        AND object_etag IS NOT NULL))
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
                content text,
                size_bytes integer NOT NULL DEFAULT 0,
                object_provider varchar(32),
                object_name varchar(1024),
                object_sha256 varchar(64),
                object_etag varchar(256),
                is_active boolean NOT NULL DEFAULT false,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS application_passwords (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                name varchar(128) NOT NULL,
                password_hash varchar(255) NOT NULL,
                created_at timestamp with time zone NOT NULL,
                last_used_at timestamp with time zone,
                revoked_at timestamp with time zone
            );

            CREATE TABLE IF NOT EXISTS oauth_grants (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                client_id varchar(128) NOT NULL,
                device_name varchar(128) NOT NULL,
                scopes text[] NOT NULL,
                created_at timestamp with time zone NOT NULL,
                last_used_at timestamp with time zone,
                revoked_at timestamp with time zone
            );

            CREATE TABLE IF NOT EXISTS oauth_tokens (
                id uuid PRIMARY KEY,
                grant_id uuid NOT NULL REFERENCES oauth_grants(id) ON DELETE CASCADE,
                token_type varchar(16) NOT NULL,
                token_hash bytea NOT NULL,
                created_at timestamp with time zone NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                last_used_at timestamp with time zone,
                revoked_at timestamp with time zone,
                replaced_by_token_id uuid,
                CONSTRAINT ck_oauth_tokens_type
                    CHECK (token_type IN ('access', 'refresh')),
                CONSTRAINT ck_oauth_tokens_hash_length
                    CHECK (octet_length(token_hash) = 32)
            );

            CREATE TABLE IF NOT EXISTS oauth_authorization_codes (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                client_id varchar(128) NOT NULL,
                redirect_uri varchar(2048) NOT NULL,
                device_name varchar(128) NOT NULL,
                scopes text[] NOT NULL,
                code_challenge varchar(128) NOT NULL,
                code_hash bytea NOT NULL,
                nonce varchar(512),
                created_at timestamp with time zone NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                consumed_at timestamp with time zone,
                CONSTRAINT ck_oauth_authorization_codes_hash_length
                    CHECK (octet_length(code_hash) = 32)
            );

            CREATE TABLE IF NOT EXISTS mfa_totp_credentials (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL UNIQUE REFERENCES users(id) ON DELETE CASCADE,
                name varchar(128) NOT NULL,
                encrypted_secret bytea NOT NULL,
                encryption_nonce bytea NOT NULL,
                encryption_tag bytea NOT NULL,
                created_at timestamp with time zone NOT NULL,
                verified_at timestamp with time zone,
                last_used_at timestamp with time zone,
                last_accepted_time_step bigint,
                revoked_at timestamp with time zone,
                CONSTRAINT ck_mfa_totp_secret_length
                    CHECK (octet_length(encrypted_secret) = 20),
                CONSTRAINT ck_mfa_totp_nonce_length
                    CHECK (octet_length(encryption_nonce) = 12),
                CONSTRAINT ck_mfa_totp_tag_length
                    CHECK (octet_length(encryption_tag) = 16)
            );

            CREATE TABLE IF NOT EXISTS mfa_recovery_codes (
                id uuid PRIMARY KEY,
                credential_id uuid NOT NULL REFERENCES mfa_totp_credentials(id) ON DELETE CASCADE,
                code_hash bytea NOT NULL,
                created_at timestamp with time zone NOT NULL,
                used_at timestamp with time zone,
                CONSTRAINT ck_mfa_recovery_code_hash_length
                    CHECK (octet_length(code_hash) = 32)
            );

            ALTER TABLE oauth_authorization_codes
                ADD COLUMN IF NOT EXISTS nonce varchar(512);

            CREATE UNIQUE INDEX IF NOT EXISTS ix_emails_queue_delivery_id
                ON emails (queue_delivery_id)
                WHERE queue_delivery_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_folders_mailbox_id_objectid
                ON folders (mailbox_id);
            CREATE INDEX IF NOT EXISTS ix_emails_email_object_id
                ON emails (email_object_id);
            CREATE INDEX IF NOT EXISTS ix_emails_thread_object_id
                ON emails (thread_object_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_folders_inbox_id_jmap_role
                ON folders (inbox_id, jmap_role)
                WHERE jmap_role IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_mail_queue_messages_state_next_attempt_at
                ON mail_queue_messages (state, next_attempt_at);
            CREATE INDEX IF NOT EXISTS ix_mail_queue_messages_received_at
                ON mail_queue_messages (received_at);
            CREATE OR REPLACE FUNCTION mk8_notify_mail_queue_ready()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $notification$
            BEGIN
                PERFORM pg_notify('mk8_mail_queue_ready', NEW.id::text);
                RETURN NEW;
            END
            $notification$;
            DO $notification$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_trigger
                    WHERE tgrelid = 'mail_queue_messages'::regclass
                      AND tgname = 'tr_mail_queue_ready'
                      AND NOT tgisinternal
                ) THEN
                    CREATE TRIGGER tr_mail_queue_ready
                    AFTER INSERT OR UPDATE OF state, next_attempt_at, lease_expires_at
                    ON mail_queue_messages
                    FOR EACH ROW
                    EXECUTE FUNCTION mk8_notify_mail_queue_ready();
                END IF;
            END
            $notification$;
            CREATE INDEX IF NOT EXISTS ix_mail_queue_recipients_message_id_state
                ON mail_queue_recipients (message_id, state);
            CREATE OR REPLACE FUNCTION mk8_notify_jmap_change_push_ready()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $notification$
            BEGIN
                PERFORM pg_notify('mk8_jmap_push_ready', NEW.account_id::text);
                RETURN NEW;
            END
            $notification$;
            CREATE OR REPLACE FUNCTION mk8_notify_jmap_subscription_push_ready()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $notification$
            BEGIN
                IF NEW.is_verified THEN
                    PERFORM pg_notify('mk8_jmap_push_ready', NEW.user_id::text);
                END IF;
                RETURN NEW;
            END
            $notification$;
            DO $notification$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = 'jmap_changes'::regclass
                      AND tgname = 'tr_jmap_change_push_ready'
                      AND NOT tgisinternal
                ) THEN
                    CREATE TRIGGER tr_jmap_change_push_ready
                    AFTER INSERT ON jmap_changes
                    FOR EACH ROW
                    EXECUTE FUNCTION mk8_notify_jmap_change_push_ready();
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = 'jmap_push_subscriptions'::regclass
                      AND tgname = 'tr_jmap_subscription_push_ready'
                      AND NOT tgisinternal
                ) THEN
                    CREATE TRIGGER tr_jmap_subscription_push_ready
                    AFTER INSERT OR UPDATE OF is_verified, next_push_at, expires_at
                    ON jmap_push_subscriptions
                    FOR EACH ROW
                    EXECUTE FUNCTION mk8_notify_jmap_subscription_push_ready();
                END IF;
            END
            $notification$;
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
            CREATE INDEX IF NOT EXISTS ix_jmap_push_subscriptions_next_push_at
                ON jmap_push_subscriptions (next_push_at)
                WHERE is_verified AND next_push_at IS NOT NULL;
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
            ALTER TABLE dav_collections
                ADD COLUMN IF NOT EXISTS is_default boolean NOT NULL DEFAULT false;
            ALTER TABLE dav_collections
                ADD COLUMN IF NOT EXISTS is_subscribed boolean NOT NULL DEFAULT true;
            ALTER TABLE dav_resources
                ADD COLUMN IF NOT EXISTS addressbook_user_id uuid;
            ALTER TABLE dav_resources
                ALTER COLUMN content DROP NOT NULL;
            ALTER TABLE dav_resources
                ADD COLUMN IF NOT EXISTS object_provider varchar(32);
            ALTER TABLE dav_resources
                ADD COLUMN IF NOT EXISTS object_name varchar(1024);
            ALTER TABLE dav_resources
                ADD COLUMN IF NOT EXISTS object_sha256 varchar(64);
            ALTER TABLE dav_resources
                ADD COLUMN IF NOT EXISTS object_etag varchar(256);
            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'dav_resources'::regclass
                      AND conname = 'ck_dav_resources_storage_shape'
                ) THEN
                    ALTER TABLE dav_resources
                        ADD CONSTRAINT ck_dav_resources_storage_shape CHECK (
                            (content IS NOT NULL
                                AND object_provider IS NULL
                                AND object_name IS NULL
                                AND object_sha256 IS NULL
                                AND object_etag IS NULL)
                            OR
                            (content IS NULL
                                AND object_provider = 'azure-blob'
                                AND object_name IS NOT NULL
                                AND object_sha256 IS NOT NULL
                                AND object_etag IS NOT NULL)) NOT VALID;
                END IF;
            END
            $migration$;
            ALTER TABLE dav_resources
                VALIDATE CONSTRAINT ck_dav_resources_storage_shape;
            UPDATE dav_collections
                SET is_default = true
                WHERE collection_type = 'addressbook'
                  AND slug = 'default'
                  AND NOT is_default
                  AND NOT EXISTS (
                      SELECT 1
                      FROM dav_collections selected
                      WHERE selected.user_id = dav_collections.user_id
                        AND selected.collection_type = 'addressbook'
                        AND selected.is_default
                  );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_collections_user_type_slug
                ON dav_collections (user_id, collection_type, slug);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_collections_user_default_addressbook
                ON dav_collections (user_id)
                WHERE collection_type = 'addressbook' AND is_default;
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
            ALTER TABLE sieve_scripts
                ALTER COLUMN content DROP NOT NULL,
                ADD COLUMN IF NOT EXISTS size_bytes integer NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS object_provider varchar(32),
                ADD COLUMN IF NOT EXISTS object_name varchar(1024),
                ADD COLUMN IF NOT EXISTS object_sha256 varchar(64),
                ADD COLUMN IF NOT EXISTS object_etag varchar(256);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sieve_scripts_user_active
                ON sieve_scripts (user_id)
                WHERE is_active;
            CREATE INDEX IF NOT EXISTS ix_application_passwords_user_name
                ON application_passwords (user_id, name);
            CREATE INDEX IF NOT EXISTS ix_application_passwords_user_id
                ON application_passwords (user_id);
            CREATE INDEX IF NOT EXISTS ix_oauth_grants_user_client
                ON oauth_grants (user_id, client_id);
            CREATE INDEX IF NOT EXISTS ix_oauth_grants_user_id
                ON oauth_grants (user_id);
            CREATE INDEX IF NOT EXISTS ix_oauth_tokens_grant_type
                ON oauth_tokens (grant_id, token_type);
            CREATE INDEX IF NOT EXISTS ix_oauth_tokens_expires_at
                ON oauth_tokens (expires_at);
            CREATE INDEX IF NOT EXISTS ix_oauth_authorization_codes_expires_at
                ON oauth_authorization_codes (expires_at);
            CREATE INDEX IF NOT EXISTS ix_mfa_recovery_codes_credential_used
                ON mfa_recovery_codes (credential_id, used_at);
            """,
            cancellationToken).ConfigureAwait(false);

        await EnsureContactUidInvariantAsync(cancellationToken).ConfigureAwait(false);

        await ValidateTableAsync(
            "mail_queue_messages",
            RequiredMessageColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "mail_queue_recipients",
            RequiredRecipientColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "emails",
            RequiredEmailColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "folders",
            RequiredFolderColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_changes",
            RequiredJmapChangeColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_blobs",
            RequiredJmapBlobColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_email_submissions",
            RequiredJmapSubmissionColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_push_subscriptions",
            RequiredJmapPushColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_vacation_responses",
            RequiredJmapVacationColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_vacation_replies",
            RequiredJmapVacationReplyColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "jmap_identities",
            RequiredJmapIdentityColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "dav_collections",
            RequiredDavCollectionColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "dav_resources",
            RequiredDavResourceColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "dav_changes",
            RequiredDavChangeColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "dav_shares",
            RequiredDavShareColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "sieve_scripts",
            RequiredSieveScriptColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "application_passwords",
            RequiredApplicationPasswordColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "oauth_grants",
            RequiredOAuthGrantColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "oauth_tokens",
            RequiredOAuthTokenColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "oauth_authorization_codes",
            RequiredOAuthAuthorizationCodeColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "mfa_totp_credentials",
            RequiredMfaTotpCredentialColumns,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            "mfa_recovery_codes",
            RequiredMfaRecoveryCodeColumns,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateTableAsync(
        string tableName,
        Dictionary<string, string> requiredColumns,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
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
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "Legacy UID repair, triggers, and unique index must remain within the same locked transaction.")]
    private async Task EnsureContactUidInvariantAsync(CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {

            // Keep the backfill, duplicate repair, triggers, and unique index in one
            // atomic boundary. The lock also prevents a protocol write from slipping
            // between legacy-data repair and index creation during an upgrade.
            await database.Database.ExecuteSqlRawAsync(
            "LOCK TABLE dav_resources IN SHARE ROW EXCLUSIVE MODE",
            cancellationToken).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync(
                """
            UPDATE dav_resources resource
            SET addressbook_user_id = CASE
                WHEN collection.collection_type = 'addressbook' THEN collection.user_id
                ELSE NULL
            END
            FROM dav_collections collection
            WHERE collection.id = resource.collection_id
              AND resource.addressbook_user_id IS DISTINCT FROM CASE
                  WHEN collection.collection_type = 'addressbook' THEN collection.user_id
                  ELSE NULL
              END
            """,
                cancellationToken).ConfigureAwait(false);

            var duplicates = await database.DavResources
                .FromSqlRaw(
                    """
                SELECT resource.*
                FROM dav_resources resource
                JOIN (
                    SELECT ranked.id
                    FROM (
                        SELECT candidate.id,
                               row_number() OVER (
                                   PARTITION BY collection.user_id, candidate.uid
                                   ORDER BY candidate.created_at, candidate.id) AS duplicate_rank
                        FROM dav_resources candidate
                        JOIN dav_collections collection
                          ON collection.id = candidate.collection_id
                        WHERE collection.collection_type = 'addressbook'
                    ) ranked
                    WHERE ranked.duplicate_rank > 1
                ) duplicate ON duplicate.id = resource.id
                ORDER BY resource.created_at, resource.id
                """)
                .Include(resource => resource.Collection)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (duplicates.Count > 0)
            {
                var affectedUsers = duplicates
                    .Select(resource => resource.AddressBookUserId!.Value)
                    .Distinct()
                    .ToArray();
                var occupiedRows = await database.DavResources
                    .AsNoTracking()
                    .Where(resource => resource.AddressBookUserId != null
                        && affectedUsers.Contains(resource.AddressBookUserId.Value))
                    .Select(resource => new { resource.AddressBookUserId, resource.Uid })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var occupied = occupiedRows
                    .GroupBy(row => row.AddressBookUserId!.Value)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(row => row.Uid).ToHashSet(StringComparer.Ordinal));

                // The loop awaits EF AddAsync, so a Span enumerator cannot safely cross it.
#pragma warning disable HLQ012
                foreach (var resource in duplicates)
                {
                    var userId = resource.AddressBookUserId!.Value;
                    var userUids = occupied[userId];
                    var replacement = $"urn:uuid:{resource.Id:D}";
                    for (var suffix = 2; userUids.Contains(replacement); suffix++)
                        replacement = $"urn:uuid:{resource.Id:D}#legacy-{suffix}";

                    var legacyContent = resource.Content
                        ?? throw new InvalidOperationException(
                            $"Legacy duplicate DAV resource {resource.Id:D} has no inline content.");
                    var content = DavContactUidMigration.Rewrite(legacyContent, replacement);
                    var now = DateTime.UtcNow;
                    var sequence = checked(++resource.Collection.SyncToken);
                    resource.Uid = replacement;
                    resource.Content = content;
                    resource.Etag = Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(content));
                    resource.SizeBytes = content.Length;
                    resource.ChangeSequence = sequence;
                    resource.UpdatedAt = now;
                    resource.Collection.UpdatedAt = now;
                    await database.DavChanges.AddAsync(new DavChangeDB
                    {
                        Id = Guid.CreateVersion7(),
                        CollectionId = resource.CollectionId,
                        Collection = resource.Collection,
                        Sequence = sequence,
                        ResourceName = resource.ResourceName,
                        IsDeleted = false,
                        Etag = resource.Etag,
                        ChangedAt = now,
                    }, cancellationToken).ConfigureAwait(false);
                    userUids.Add(replacement);
                }
#pragma warning restore HLQ012

                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await database.Database.ExecuteSqlRawAsync(
                """
            CREATE OR REPLACE FUNCTION set_dav_resource_addressbook_user_id()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                owning_user uuid;
                owning_type varchar(16);
            BEGIN
                SELECT user_id, collection_type
                INTO STRICT owning_user, owning_type
                FROM dav_collections
                WHERE id = NEW.collection_id;
                NEW.addressbook_user_id := CASE
                    WHEN owning_type = 'addressbook' THEN owning_user
                    ELSE NULL
                END;
                RETURN NEW;
            END;
            $function$;

            DROP TRIGGER IF EXISTS set_dav_resource_addressbook_user_id
                ON dav_resources;
            CREATE TRIGGER set_dav_resource_addressbook_user_id
                BEFORE INSERT OR UPDATE OF collection_id, addressbook_user_id
                ON dav_resources
                FOR EACH ROW
                EXECUTE FUNCTION set_dav_resource_addressbook_user_id();

            CREATE OR REPLACE FUNCTION propagate_dav_collection_uid_scope()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF OLD.user_id IS DISTINCT FROM NEW.user_id
                   OR OLD.collection_type IS DISTINCT FROM NEW.collection_type THEN
                    UPDATE dav_resources
                    SET addressbook_user_id = CASE
                        WHEN NEW.collection_type = 'addressbook' THEN NEW.user_id
                        ELSE NULL
                    END
                    WHERE collection_id = NEW.id;
                END IF;
                RETURN NEW;
            END;
            $function$;

            DROP TRIGGER IF EXISTS propagate_dav_collection_uid_scope
                ON dav_collections;
            CREATE TRIGGER propagate_dav_collection_uid_scope
                AFTER UPDATE OF user_id, collection_type
                ON dav_collections
                FOR EACH ROW
                EXECUTE FUNCTION propagate_dav_collection_uid_scope();

            CREATE UNIQUE INDEX IF NOT EXISTS ix_dav_resources_addressbook_user_uid
                ON dav_resources (addressbook_user_id, uid)
                WHERE addressbook_user_id IS NOT NULL;
            """,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

}
