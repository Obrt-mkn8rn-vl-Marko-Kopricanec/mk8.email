using Npgsql;

namespace mk8.email.Messaging.Tests;

internal static class NativeHealthObservation
{
    internal static async Task<HttpResponseMessage> ObserveRequestAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<CancellationToken, Task<NativeHealthPoint>> collect,
        Action<NativeHealthPoint> emit,
        CancellationToken cancellationToken)
    {
        try { return await send(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await TryCollectAsync(collect, emit).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task TryCollectAsync(Func<CancellationToken, Task<NativeHealthPoint>> collect,
        Action<NativeHealthPoint> emit)
    {
        // Independent cooperative attempt; unpooled connection/command bounds below
        // are not a hard physical two-second SLA or arbitrary hung-work termination.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { emit(await collect(deadline.Token).ConfigureAwait(false)); }
        // Secondary ordinary collector/sink faults cannot replace original cancellation.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { }
    }

    internal static async Task<NativeHealthPoint> ReadAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = false, Timeout = 2, CommandTimeout = 2 }.ConnectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM gateway_traffic_records WHERE protocol='health' AND direction='inbound'),
                (SELECT count(*) FROM gateway_traffic_records WHERE protocol='health' AND direction='outbound'),
                count(*) FILTER (WHERE state='pending'),
                count(*) FILTER (WHERE state='processing'),
                count(*) FILTER (WHERE state='completed'),
                count(*) FILTER (WHERE state='failed'),
                count(*) FILTER (WHERE state='expired'),
                count(*) FILTER (WHERE response_payload_blob_name IS NOT NULL)
            FROM application_requests WHERE protocol='health' AND operation='system.ping'
            """;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLifetime = reader.ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The owned health-count point is unavailable.");
        return new NativeHealthPoint(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }
}
