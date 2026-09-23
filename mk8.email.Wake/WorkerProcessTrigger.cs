using System.Diagnostics;

namespace mk8.email.Wake;

internal sealed class WorkerProcessTrigger(
    string workerAssemblyPath,
    string workerConfigPath) : IWorkerWakeAction
{
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(workerAssemblyPath)
            || !Path.IsPathFullyQualified(workerConfigPath)
            || !File.Exists(workerAssemblyPath)
            || !File.Exists(workerConfigPath))
        {
            throw new InvalidOperationException(
                "The on-demand Worker assembly or configuration file is missing.");
        }
    }

    public async Task SignalAsync(CancellationToken cancellationToken)
    {
        Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var dotnetHost = Environment.ProcessPath is { } path
            && string.Equals(Path.GetFileName(path), "dotnet", StringComparison.Ordinal)
                ? path
                : "dotnet";
        var start = new ProcessStartInfo(dotnetHost)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(workerAssemblyPath)!,
        };
        start.ArgumentList.Add(workerAssemblyPath);
        start.ArgumentList.Add("--drain");
        start.Environment["MK8EMAIL_CONFIG_FILE"] = workerConfigPath;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The on-demand Worker could not start.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The on-demand Worker drain failed.");
    }
}
