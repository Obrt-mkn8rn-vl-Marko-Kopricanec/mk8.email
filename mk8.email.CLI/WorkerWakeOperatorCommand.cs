using mk8.email.Configuration;
using mk8.email.Hosting;
using Npgsql;

namespace mk8.email.CLI;

internal static class WorkerWakeOperatorCommand
{
    internal static bool Matches(string[] arguments) => arguments switch
    {
        ["--worker-wake-schema-state" or "--prepare-worker-wake"] => true,
        ["--restore-worker-wake", "legacy" or "receipts"] => true,
        _ => false,
    };

    internal static async Task ExecuteAsync(string[] arguments, EnvironmentConfig environment)
    {
        RequireOperator();
        if (!environment.Messaging.Enabled)
            throw new InvalidOperationException("Distributed messaging must be enabled.");
        var role = GetWakeRole(environment);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var source = NpgsqlDataSource.Create(environment.BuildConnectionString());
        await using var sourceLifetime = source.ConfigureAwait(false);
        if (arguments[0] is "--worker-wake-schema-state")
        {
            var state = await WorkerWakeSchemaTransition.ReadAsync(source, role, timeout.Token).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(state == WorkerWakeSchemaState.Legacy ? "legacy" : "receipts").ConfigureAwait(false);
        }
        else if (arguments[0] is "--prepare-worker-wake")
            await WorkerWakeSchemaTransition.EnableAsync(source, role, timeout.Token).ConfigureAwait(false);
        else
        {
            var state = arguments[1] switch
            {
                "legacy" => WorkerWakeSchemaState.Legacy,
                "receipts" => WorkerWakeSchemaState.Receipts,
                _ => throw new ArgumentException("The prior Wake schema state must be legacy or receipts.", nameof(arguments)),
            };
            await WorkerWakeSchemaTransition.RestoreAsync(source, role, state, timeout.Token).ConfigureAwait(false);
        }
    }

    internal static void RequireOperator()
    {
        if (!OperatingSystem.IsLinux() || !string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
            throw new InvalidOperationException("Wake schema transitions require a root operator, not the Worker service.");
    }

    internal static string GetWakeRole(EnvironmentConfig environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var wake = environment.Management;
        var worker = environment.Database;
        if (!string.Equals(wake.WakeDatabaseHost, worker.Host, StringComparison.Ordinal)
            || wake.WakeDatabasePort != worker.Port
            || !string.Equals(wake.WakeDatabaseName, worker.Name, StringComparison.Ordinal)
            || string.Equals(wake.WakeDatabaseUsername, worker.Username, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(wake.WakeDatabaseUsername))
            throw new InvalidOperationException("Wake and Worker must use separate roles on the same explicit database endpoint.");
        return wake.WakeDatabaseUsername;
    }
}
