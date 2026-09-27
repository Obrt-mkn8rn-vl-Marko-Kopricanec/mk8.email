using mk8.email.Configuration;
using mk8.email.Hosting;
using Npgsql;

namespace mk8.email.CLI;

internal static class WorkerWakeOperatorCommand
{
    internal static bool Matches(string[] arguments) =>
        arguments.Length == 3 && arguments[0] is ("--worker-wake-schema-state" or "--prepare-worker-wake")
        || arguments.Length == 4 && arguments[0] is "--restore-worker-wake";

    internal static async Task ExecuteAsync(string[] arguments, bool development)
    {
        if (!development && (!OperatingSystem.IsLinux()
            || !string.Equals(Environment.UserName, "root", StringComparison.Ordinal)))
            throw new InvalidOperationException("Wake schema transitions require a root operator, not the Worker service.");
        var environment = EnvironmentLoader.LoadFromFile(arguments[1], development, EnvironmentValidationRole.ApplicationWorker);
        if (!environment.Messaging.Enabled)
            throw new InvalidOperationException("Distributed messaging must be enabled.");
        var role = await ReadRoleAsync(arguments[2], environment, development).ConfigureAwait(false);
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
            var state = arguments[3] switch
            {
                "legacy" => WorkerWakeSchemaState.Legacy,
                "receipts" => WorkerWakeSchemaState.Receipts,
                _ => throw new ArgumentException("The prior Wake schema state must be legacy or receipts.", nameof(arguments)),
            };
            await WorkerWakeSchemaTransition.RestoreAsync(source, role, state, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadRoleAsync(string path, EnvironmentConfig environment, bool development)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || new FileInfo(path).Length is < 1 or > 32_768)
            throw new InvalidOperationException("The Wake connection file is missing, oversized or unsafe.");
        if (!development)
        {
            if (!OperatingSystem.IsLinux())
                throw new InvalidOperationException("Production Wake connection-file validation requires Linux.");
            var forbidden = UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & forbidden) != UnixFileMode.None)
                throw new InvalidOperationException("The Wake connection file has unsafe permissions.");
        }
        var wake = ParseConnection((await File.ReadAllTextAsync(path).ConfigureAwait(false)).TrimEnd('\r', '\n'));
        var worker = new NpgsqlConnectionStringBuilder(environment.BuildConnectionString());
        if (!string.Equals(wake.Host, worker.Host, StringComparison.Ordinal) || wake.Port != worker.Port
            || !string.Equals(wake.Database, worker.Database, StringComparison.Ordinal)
            || string.Equals(wake.Username, worker.Username, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(wake.Username))
            throw new InvalidOperationException("Wake and Worker must use separate roles on the same explicit database endpoint.");
        return wake.Username;
    }

    private static NpgsqlConnectionStringBuilder ParseConnection(string value)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            // A wrong secret-file path must never echo its contents through a parser exception.
            throw new InvalidOperationException("The Wake file is not a valid PostgreSQL connection string.");
        }
    }
}
