using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

internal sealed class WorkerDrainRunner(
    IApplicationRequestConsumer requests,
    ApplicationRequestWorker application,
    ApplicationWorkerIdentity identity,
    Func<CancellationToken, Task<bool>> processMailQueue,
    Func<CancellationToken, Task> cleanupMailQueue,
    Func<CancellationToken, Task>? processJmapPush = null)
{
    public async Task<WorkerDrainResult> RunAsync(CancellationToken cancellationToken)
    {
        var requestCount = 0;
        var mailCount = 0;
        var idleScans = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = await requests.TryClaimAsync(identity.WorkerId, cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                await application.ProcessLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
                requestCount++;
            }

            var processedMail = await processMailQueue(cancellationToken).ConfigureAwait(false);
            if (processedMail)
                mailCount++;
            await cleanupMailQueue(cancellationToken).ConfigureAwait(false);
            if (processJmapPush is not null)
                await processJmapPush(cancellationToken).ConfigureAwait(false);

            if (lease is not null || processedMail)
            {
                idleScans = 0;
                continue;
            }

            idleScans++;
            if (idleScans == 2)
                return new WorkerDrainResult(requestCount, mailCount);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }
}
