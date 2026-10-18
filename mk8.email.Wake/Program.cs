using System.Runtime.InteropServices;
using mk8.email.Configuration;
using mk8.email.Wake;
using Npgsql;

var mode = WakeBootstrap.ParseMode(args);
if (mode is null)
{
    await Console.Error.WriteLineAsync("Usage: mk8.email.Wake --serve|--probe-db; service values come only from installation-local Environment/.env.")
        .ConfigureAwait(false);
    return 2;
}

try
{
    if (!OperatingSystem.IsLinux())
        throw new PlatformNotSupportedException("The Wake service currently requires Linux signal ownership.");
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
    try
    {
        var config = await PortableEnvironmentLoader.LoadWakeAsync(shutdown.Token).ConfigureAwait(false);
        var trigger = new WorkerProcessTrigger(config.DotnetHostPath, config.WorkerAssemblyPath);
        trigger.Validate();
        var dataSource = NpgsqlDataSource.Create(config.BuildConnectionString());
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(dataSource, shutdown.Token).ConfigureAwait(false);
        var probe = new WorkerWakeProbe(dataSource, config.IncludeJmap);
        if (mode == WakeMode.Probe)
        {
            var snapshot = await probe.ReadAsync(shutdown.Token).ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"Worker wake backend is available; due={(snapshot.HasDueWork ? "true" : "false")}.")
                .ConfigureAwait(false);
            return 0;
        }
        var monitor = new WorkerWakeMonitor(dataSource, probe, trigger, TimeProvider.System);
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
    await Console.Error.WriteLineAsync($"The Worker wake service failed: {exception.GetType().Name}.").ConfigureAwait(false);
    return 1;
}
