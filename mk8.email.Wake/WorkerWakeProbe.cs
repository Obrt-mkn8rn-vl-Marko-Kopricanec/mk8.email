using Npgsql;

namespace mk8.email.Wake;

public sealed record WorkerWakeSnapshot(bool HasDueWork, DateTimeOffset? NextDueAt);

public sealed class WorkerWakeProbe(NpgsqlDataSource dataSource, bool includeJmap = true)
{
    public async Task<WorkerWakeSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            WITH tick AS (SELECT clock_timestamp() AS at_time)
            SELECT
                EXISTS (
                    SELECT 1 FROM application_requests r, tick
                    WHERE r.state = 'pending'
                       OR (r.state = 'processing'
                           AND (r.lease_expires_at <= tick.at_time
                                OR r.deadline_at <= tick.at_time))
                )
                OR EXISTS (
                    SELECT 1 FROM mail_queue_messages q, tick
                    WHERE q.next_attempt_at <= tick.at_time
                      AND (q.state = 'pending'
                           OR (q.state = 'processing'
                               AND q.lease_expires_at <= tick.at_time))
                )
                OR (@jmap_enabled AND EXISTS (
                    SELECT 1 FROM jmap_push_subscriptions s, tick
                    WHERE s.expires_at <= tick.at_time
                       OR (s.is_verified AND s.next_push_at <= tick.at_time)
                ))
                OR (@jmap_enabled AND EXISTS (
                    SELECT 1
                    FROM jmap_push_subscriptions s
                    JOIN users u ON u.id = s.user_id AND u.is_active
                    JOIN inboxes i ON i.owner_id = u.id
                        AND i.alias_for_inbox_id IS NULL AND i.name <> '*'
                    JOIN addresses a ON a.id = i.address_id AND a.is_active
                    JOIN companies c ON c.id = a.company_id AND c.is_active
                    JOIN jmap_changes change ON change.account_id = i.id
                        AND change.sequence > s.last_pushed_change
                    CROSS JOIN tick
                    WHERE s.is_verified AND s.expires_at > tick.at_time
                      AND (s.next_push_at IS NULL OR s.next_push_at <= tick.at_time)
                )) AS has_due_work,
                LEAST(
                    (SELECT min(r.deadline_at) FROM application_requests r
                     WHERE r.state IN ('pending', 'processing')),
                    (SELECT min(r.lease_expires_at) FROM application_requests r
                     WHERE r.state = 'processing'),
                    (SELECT min(q.next_attempt_at) FROM mail_queue_messages q
                     WHERE q.state = 'pending'),
                    (SELECT min(GREATEST(q.next_attempt_at, q.lease_expires_at))
                     FROM mail_queue_messages q WHERE q.state = 'processing'),
                    CASE WHEN @jmap_enabled THEN
                        (SELECT min(s.next_push_at) FROM jmap_push_subscriptions s
                         WHERE s.is_verified)
                    END,
                    CASE WHEN @jmap_enabled THEN
                        (SELECT min(s.expires_at) FROM jmap_push_subscriptions s)
                    END
                ) AS next_due_at
            """);
        command.Parameters.AddWithValue("jmap_enabled", includeJmap);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The Worker wake query returned no result.");
        return new WorkerWakeSnapshot(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetDateTime(1)));
    }
}
