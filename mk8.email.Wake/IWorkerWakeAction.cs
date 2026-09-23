namespace mk8.email.Wake;

internal interface IWorkerWakeAction
{
    Task SignalAsync(CancellationToken cancellationToken);
}
