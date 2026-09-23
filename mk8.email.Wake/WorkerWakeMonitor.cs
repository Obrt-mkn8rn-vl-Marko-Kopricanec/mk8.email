using Npgsql;

namespace mk8.email.Wake;

internal sealed class WorkerWakeMonitor(
    NpgsqlDataSource dataSource,
    WorkerWakeProbe probe,
    IWorkerWakeAction wakeAction,
    TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var listener = await dataSource.OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var listenerLifetime = listener.ConfigureAwait(false);
                var listen = listener.CreateCommand();
                await using var listenLifetime = listen.ConfigureAwait(false);
                listen.CommandText = """
                    LISTEN mk8_application_request;
                    LISTEN mk8_mail_queue_ready;
                    LISTEN mk8_jmap_push_ready;
                    """;
                await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var snapshot = await probe.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (snapshot.HasDueWork)
                        await wakeAction.SignalAsync(cancellationToken).ConfigureAwait(false);

                    var wait = snapshot.HasDueWork
                        ? TimeSpan.FromSeconds(2)
                        : snapshot.NextDueAt is { } next
                            ? next - timeProvider.GetUtcNow()
                            : TimeSpan.FromSeconds(30);
                    var milliseconds = wait.TotalMilliseconds switch
                    {
                        <= 1 => 1,
                        >= 30_000 => 30_000,
                        var value => (int)Math.Ceiling(value),
                    };
                    await listener.WaitAsync(milliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Wake must recover after any transient database/listener failure.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                await Console.Error.WriteLineAsync(
                    $"The Worker wake monitor will retry after {exception.GetType().Name}.")
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
