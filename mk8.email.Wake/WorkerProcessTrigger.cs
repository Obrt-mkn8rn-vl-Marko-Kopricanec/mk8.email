using System.Diagnostics;

namespace mk8.email.Wake;

internal sealed class WorkerProcessTrigger(
    string dotnetHostPath,
    string workerAssemblyPath) : IWorkerWakeAction
{
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(dotnetHostPath)
            || !Path.IsPathFullyQualified(workerAssemblyPath)
            || !File.Exists(dotnetHostPath)
            || !File.Exists(workerAssemblyPath))
        {
            throw new InvalidOperationException(
                "The explicitly configured dotnet host or Worker assembly is missing.");
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        Validate();
        var start = new ProcessStartInfo(dotnetHostPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(workerAssemblyPath)!,
        };
        start.ArgumentList.Add(workerAssemblyPath);
        start.ArgumentList.Add("--drain");
        // The Worker independently loads its own fixed AppContext root. No
        // inherited service values, config locator or PATH lookup is forwarded.
        start.Environment.Clear();
        return start;
    }

    public async Task SignalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = CreateStartInfo();
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
