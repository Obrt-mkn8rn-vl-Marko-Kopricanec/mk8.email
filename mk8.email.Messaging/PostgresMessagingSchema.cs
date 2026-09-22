using Npgsql;

namespace mk8.email.Messaging;

public static class PostgresMessagingSchema
{
    private const long SchemaLockId = 5_563_539_125_731_845_288;

    public static async Task EnsureAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var schemaLock = connection.CreateCommand())
        {
            schemaLock.Transaction = transaction;
            schemaLock.CommandText = "SELECT pg_advisory_xact_lock(@lock_id)";
            schemaLock.Parameters.AddWithValue("lock_id", SchemaLockId);
            await schemaLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS gateway_traffic_records (
            id uuid PRIMARY KEY,
            session_id uuid NOT NULL,
            sequence bigint NOT NULL,
            direction varchar(8) NOT NULL,
            protocol varchar(32) NOT NULL,
            content_type varchar(255) NOT NULL,
            application_request_id uuid NULL,
            encryption_key_id varchar(64) NOT NULL,
            payload_inline bytea NULL,
            payload_blob_provider varchar(32) NULL,
            payload_blob_name varchar(1024) NULL,
            payload_blob_etag varchar(256) NULL,
            payload_length bigint NOT NULL,
            payload_nonce bytea NOT NULL,
            payload_tag bytea NOT NULL,
            payload_sha256 char(64) NOT NULL,
            metadata jsonb NOT NULL,
            recorded_at timestamp with time zone NOT NULL,
            schema_version smallint NOT NULL DEFAULT 1,
            CONSTRAINT uq_gateway_traffic_session_sequence UNIQUE (session_id, sequence),
            CONSTRAINT ck_gateway_traffic_sequence CHECK (sequence >= 0),
            CONSTRAINT ck_gateway_traffic_direction CHECK (direction IN ('inbound', 'outbound')),
            CONSTRAINT ck_gateway_traffic_payload_location CHECK (
                (payload_inline IS NOT NULL
                    AND payload_blob_provider IS NULL
                    AND payload_blob_name IS NULL
                    AND payload_blob_etag IS NULL
                    AND payload_length = octet_length(payload_inline))
                OR (payload_inline IS NULL
                    AND payload_blob_provider = 'azure-blob'
                    AND payload_blob_name IS NOT NULL
                    AND payload_blob_etag IS NOT NULL
                    AND payload_length >= 0)),
            CONSTRAINT ck_gateway_traffic_nonce CHECK (octet_length(payload_nonce) = 12),
            CONSTRAINT ck_gateway_traffic_tag CHECK (octet_length(payload_tag) = 16),
            CONSTRAINT ck_gateway_traffic_hash CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
            CONSTRAINT ck_gateway_traffic_metadata CHECK (jsonb_typeof(metadata) = 'object'),
            CONSTRAINT ck_gateway_traffic_schema CHECK (schema_version = 1)
        );

        CREATE INDEX IF NOT EXISTS ix_gateway_traffic_session_recorded
            ON gateway_traffic_records (session_id, recorded_at, sequence);
        CREATE INDEX IF NOT EXISTS ix_gateway_traffic_application_request
            ON gateway_traffic_records (application_request_id)
            WHERE application_request_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS application_requests (
            id uuid PRIMARY KEY,
            session_id uuid NOT NULL,
            sequence bigint NOT NULL,
            protocol varchar(32) NOT NULL,
            operation varchar(128) NOT NULL,
            request_content_type varchar(255) NOT NULL,
            request_encryption_key_id varchar(64) NOT NULL,
            request_payload_inline bytea NULL,
            request_payload_blob_provider varchar(32) NULL,
            request_payload_blob_name varchar(1024) NULL,
            request_payload_blob_etag varchar(256) NULL,
            request_payload_length bigint NOT NULL,
            request_payload_nonce bytea NOT NULL,
            request_payload_tag bytea NOT NULL,
            request_payload_sha256 char(64) NOT NULL,
            request_metadata jsonb NOT NULL,
            idempotency_key varchar(128) NULL,
            created_at timestamp with time zone NOT NULL,
            deadline_at timestamp with time zone NOT NULL,
            state varchar(16) NOT NULL DEFAULT 'pending',
            attempt_count integer NOT NULL DEFAULT 0,
            lease_owner varchar(128) NULL,
            lease_expires_at timestamp with time zone NULL,
            response_content_type varchar(255) NULL,
            response_encryption_key_id varchar(64) NULL,
            response_payload_inline bytea NULL,
            response_payload_blob_provider varchar(32) NULL,
            response_payload_blob_name varchar(1024) NULL,
            response_payload_blob_etag varchar(256) NULL,
            response_payload_length bigint NULL,
            response_payload_nonce bytea NULL,
            response_payload_tag bytea NULL,
            response_payload_sha256 char(64) NULL,
            response_metadata jsonb NULL,
            response_is_error boolean NULL,
            error_code varchar(128) NULL,
            error_detail varchar(2048) NULL,
            completed_at timestamp with time zone NULL,
            schema_version smallint NOT NULL DEFAULT 1,
            CONSTRAINT uq_application_request_session_sequence UNIQUE (session_id, sequence),
            CONSTRAINT ck_application_request_sequence CHECK (sequence >= 0),
            CONSTRAINT ck_application_request_deadline CHECK (deadline_at > created_at),
            CONSTRAINT ck_application_request_state CHECK (
                state IN ('pending', 'processing', 'completed', 'failed', 'expired')),
            CONSTRAINT ck_application_request_attempt CHECK (attempt_count >= 0),
            CONSTRAINT ck_application_request_payload_location CHECK (
                (request_payload_inline IS NOT NULL
                    AND request_payload_blob_provider IS NULL
                    AND request_payload_blob_name IS NULL
                    AND request_payload_blob_etag IS NULL
                    AND request_payload_length = octet_length(request_payload_inline))
                OR (request_payload_inline IS NULL
                    AND request_payload_blob_provider = 'azure-blob'
                    AND request_payload_blob_name IS NOT NULL
                    AND request_payload_blob_etag IS NOT NULL
                    AND request_payload_length >= 0)),
            CONSTRAINT ck_application_request_nonce CHECK (octet_length(request_payload_nonce) = 12),
            CONSTRAINT ck_application_request_tag CHECK (octet_length(request_payload_tag) = 16),
            CONSTRAINT ck_application_request_hash CHECK (
                request_payload_sha256 ~ '^[0-9a-f]{64}$'),
            CONSTRAINT ck_application_request_metadata CHECK (
                jsonb_typeof(request_metadata) = 'object'),
            CONSTRAINT ck_application_request_lease CHECK (
                (state = 'processing' AND lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL)
                OR (state <> 'processing' AND lease_owner IS NULL AND lease_expires_at IS NULL)),
            CONSTRAINT ck_application_request_response CHECK (
                (state = 'completed'
                    AND response_content_type IS NOT NULL
                    AND response_encryption_key_id IS NOT NULL
                    AND response_payload_length IS NOT NULL
                    AND ((response_payload_inline IS NOT NULL
                            AND response_payload_blob_provider IS NULL
                            AND response_payload_blob_name IS NULL
                            AND response_payload_blob_etag IS NULL
                            AND response_payload_length = octet_length(response_payload_inline))
                        OR (response_payload_inline IS NULL
                            AND response_payload_blob_provider = 'azure-blob'
                            AND response_payload_blob_name IS NOT NULL
                            AND response_payload_blob_etag IS NOT NULL
                            AND response_payload_length >= 0))
                    AND octet_length(response_payload_nonce) = 12
                    AND octet_length(response_payload_tag) = 16
                    AND response_payload_sha256 ~ '^[0-9a-f]{64}$'
                    AND jsonb_typeof(response_metadata) = 'object'
                    AND response_is_error IS NOT NULL
                    AND completed_at IS NOT NULL)
                OR (state <> 'completed'
                    AND response_content_type IS NULL
                    AND response_encryption_key_id IS NULL
                    AND response_payload_inline IS NULL
                    AND response_payload_blob_provider IS NULL
                    AND response_payload_blob_name IS NULL
                    AND response_payload_blob_etag IS NULL
                    AND response_payload_length IS NULL
                    AND response_payload_nonce IS NULL
                    AND response_payload_tag IS NULL
                    AND response_payload_sha256 IS NULL
                    AND response_metadata IS NULL
                    AND response_is_error IS NULL)),
            CONSTRAINT ck_application_request_terminal CHECK (
                (state IN ('completed', 'failed', 'expired') AND completed_at IS NOT NULL)
                OR (state IN ('pending', 'processing') AND completed_at IS NULL)),
            CONSTRAINT ck_application_request_schema CHECK (schema_version = 1)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_application_request_idempotency
            ON application_requests (protocol, idempotency_key)
            WHERE idempotency_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_application_request_claim
            ON application_requests (state, deadline_at, created_at, id)
            WHERE state IN ('pending', 'processing');
        CREATE INDEX IF NOT EXISTS ix_application_request_completed
            ON application_requests (completed_at)
            WHERE completed_at IS NOT NULL;
        """;
}
