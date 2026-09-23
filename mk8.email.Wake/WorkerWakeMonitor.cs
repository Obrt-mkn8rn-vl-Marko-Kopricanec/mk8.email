using Npgsql;

namespace mk8.email.Wake;

public sealed class WorkerWakeMonitor(
    NpgsqlDataSource dataSource,
    WorkerWakeProbe probe,
    WorkerWakeTrigger trigger,
    TimeProvider timeProvider)
{
    private static readonly string[] Channels =
    [
        "mk8_application_request",
        "mk8_mail_queue_ready",
        "mk8_jmap_push_ready",
    ];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var listener = await dataSource.OpenConnectionAsync(cancellationToken);
                foreach (var channel in Channels)
                {
                    await using var listen = listener.CreateCommand();
                    listen.CommandText = $"LISTEN {channel}";
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var snapshot = await probe.ReadAsync(cancellationToken);
                    if (snapshot.HasDueWork)
                        trigger.Signal();

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
                    await listener.WaitAsync(milliseconds, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"The Worker wake monitor will retry after {exception.GetType().Name}.");
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, cancellationToken);
            }
        }
    }
}
