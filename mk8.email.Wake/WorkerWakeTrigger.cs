namespace mk8.email.Wake;

internal sealed class WorkerWakeTrigger(string directory) : IWorkerWakeAction
{
    public Task SignalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Signal();
        return Task.CompletedTask;
    }

    public void Validate()
    {
        if (!Path.IsPathFullyQualified(directory)
            || !Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != FileAttributes.None)
        {
            throw new InvalidOperationException("The Worker wake directory is missing or unsafe.");
        }
    }

    public void Signal()
    {
        Validate();
        var trigger = Path.Combine(directory, "trigger");
        try
        {
            using var stream = new FileStream(
                trigger,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException) when (File.Exists(trigger))
        {
            // A previous unconsumed trigger already keeps DirectoryNotEmpty true.
        }
    }
}
