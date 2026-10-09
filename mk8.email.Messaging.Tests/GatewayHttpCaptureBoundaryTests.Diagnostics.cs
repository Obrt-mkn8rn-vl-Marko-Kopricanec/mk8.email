using System.Text.Json;
using Npgsql;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayHttpCaptureBoundaryTests
{
    internal sealed partial class CaptureFixture
    {
        public GatewayFixtureDiagnostics Diagnostics { get; }

        // One owned independent connection, no pooled dependency or raw query/payload disclosure.
        // Both the connection/command limits and the independent collection cancellation are bounded.
        private async Task<string> CaptureFailureSnapshotAsync(CancellationToken cancellationToken)
        {
            var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ApplicationConnection)
            { Pooling = false, Timeout = 2, CommandTimeout = 1, CancellationTimeout = 1000 }.ConnectionString);
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = """
                SELECT jsonb_build_object(
                    'queue', (SELECT jsonb_build_object(
                        'pending', count(*) FILTER (WHERE state='pending'),
                        'processing', count(*) FILTER (WHERE state='processing'),
                        'completed', count(*) FILTER (WHERE state='completed'),
                        'failed', count(*) FILTER (WHERE state='failed'),
                        'expired', count(*) FILTER (WHERE state='expired'),
                        'maximumAttempts', coalesce(max(attempt_count),0)) FROM application_requests),
                    'journal', (SELECT jsonb_build_object(
                        'presentationInbound', count(*) FILTER (WHERE metadata->>'layer'='presentation' AND direction='inbound'),
                        'presentationOutbound', count(*) FILTER (WHERE metadata->>'layer'='presentation' AND direction='outbound'),
                        'total', count(*)) FROM gateway_traffic_records),
                    'databaseActivity', (SELECT jsonb_build_object(
                        'active', count(*) FILTER (WHERE state='active'),
                        'waitingLock', count(*) FILTER (WHERE wait_event_type='Lock'),
                        'waitingIO', count(*) FILTER (WHERE wait_event_type='IO'),
                        'idleInTransaction', count(*) FILTER (WHERE state='idle in transaction'))
                        FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid()))
                """;
            using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
            return Diagnostics.Report(document.RootElement, _worker.ExecuteTask?.Status.ToString() ?? "NotStarted");
        }
    }
}
