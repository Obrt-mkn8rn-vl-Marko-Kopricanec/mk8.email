using System.Runtime.InteropServices;
using mk8.email.Wake;
using Npgsql;

if (args.Length is < 3 or > 4
    || args[0] is not ("--serve" or "--probe")
    || (args.Length == 4
        && !string.Equals(args[3], "--without-jmap", StringComparison.Ordinal)))
{
    await Console.Error.WriteLineAsync(
        "Usage: mk8.email.Wake --serve|--probe CONNECTION_STRING_FILE TRIGGER_DIRECTORY [--without-jmap]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    if (!Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
        throw new ArgumentException(
            "The connection file and wake directory must be absolute paths.", nameof(args));
    var connectionString = (await File.ReadAllTextAsync(args[1]).ConfigureAwait(false))
        .TrimEnd('\r', '\n');
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("The Worker wake database connection file is empty.");
    var dataSource = NpgsqlDataSource.Create(connectionString);
    await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
    await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(dataSource).ConfigureAwait(false);
    var probe = new WorkerWakeProbe(dataSource, includeJmap: args.Length == 3);
    var trigger = new WorkerWakeTrigger(args[2]);
    trigger.Validate();
    if (string.Equals(args[0], "--probe", StringComparison.Ordinal))
    {
        var snapshot = await probe.ReadAsync().ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"Worker wake backend is available; due={(snapshot.HasDueWork ? "true" : "false")}.")
            .ConfigureAwait(false);
        return 0;
    }

    using var shutdown = new CancellationTokenSource();
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal =>
    {
        signal.Cancel = true;
        shutdown.Cancel();
    });
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
    {
        signal.Cancel = true;
        shutdown.Cancel();
    });
    var monitor = new WorkerWakeMonitor(dataSource, probe, trigger, TimeProvider.System);
    try
    {
        await monitor.RunAsync(shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    return 0;
}
#pragma warning disable CA1031 // A process entry point must sanitize every unexpected failure.
catch (Exception exception)
#pragma warning restore CA1031
{
    await Console.Error.WriteLineAsync(
        $"The Worker wake service failed: {exception.GetType().Name}.")
        .ConfigureAwait(false);
    return 1;
}
