using System.Runtime.InteropServices;
using mk8.email.Wake;
using Npgsql;

if (args.Length is < 3 or > 4
    || args[0] is not ("--serve" or "--probe")
    || (args.Length == 4 && args[3] != "--without-jmap"))
{
    Console.Error.WriteLine(
        "Usage: mk8.email.Wake --serve|--probe CONNECTION_STRING_FILE TRIGGER_DIRECTORY [--without-jmap]");
    return 2;
}

try
{
    if (!Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
        throw new ArgumentException("The connection file and wake directory must be absolute paths.");
    var connectionString = File.ReadAllText(args[1]).TrimEnd('\r', '\n');
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("The Worker wake database connection file is empty.");
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(dataSource);
    var probe = new WorkerWakeProbe(dataSource, includeJmap: args.Length == 3);
    var trigger = new WorkerWakeTrigger(args[2]);
    trigger.Validate();
    if (args[0] == "--probe")
    {
        var snapshot = await probe.ReadAsync();
        Console.WriteLine(
            $"Worker wake backend is available; due={snapshot.HasDueWork.ToString().ToLowerInvariant()}.");
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
        await monitor.RunAsync(shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"The Worker wake service failed: {exception.GetType().Name}.");
    return 1;
}
