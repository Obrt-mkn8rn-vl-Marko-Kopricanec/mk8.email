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
            ["failure_notice_created"] = "bool",
            ["completed_at"] = "timestamptz",
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredEmailColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["queue_delivery_id"] = "uuid",
            ["keywords"] = "_text",
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
                failure_notice_created boolean NOT NULL DEFAULT false,
                completed_at timestamp with time zone,
                CONSTRAINT ck_mail_queue_recipients_state
                    CHECK (state IN ('pending', 'delivered', 'permanent_failure', 'quarantined')),
                CONSTRAINT ck_mail_queue_recipients_attempt_count
                    CHECK (attempt_count >= 0)
            );

            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS queue_delivery_id uuid;
            ALTER TABLE emails
                ADD COLUMN IF NOT EXISTS keywords text[] NOT NULL DEFAULT ARRAY[]::text[];

            CREATE UNIQUE INDEX IF NOT EXISTS ix_emails_queue_delivery_id
                ON emails (queue_delivery_id)
                WHERE queue_delivery_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_mail_queue_messages_state_next_attempt_at
                ON mail_queue_messages (state, next_attempt_at);
            CREATE INDEX IF NOT EXISTS ix_mail_queue_messages_received_at
                ON mail_queue_messages (received_at);
            CREATE INDEX IF NOT EXISTS ix_mail_queue_recipients_message_id_state
                ON mail_queue_recipients (message_id, state);
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
