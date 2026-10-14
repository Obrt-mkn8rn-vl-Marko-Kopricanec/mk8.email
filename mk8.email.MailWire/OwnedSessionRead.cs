using System.Runtime.ExceptionServices;

namespace mk8.email.MailWire;

// The command body cannot leave a pending stream/journal read behind on any exit.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0182", Justification = "The IMAP friend assembly invokes this shared internal owner; actual authenticated IDLE controls and managed child-fault controls execute it. A file-local class would break those consumers.")]
internal static class OwnedSessionRead
{
    internal static async Task RunAsync(
        Func<CancellationToken, Task<BoundedLine>> read,
        Func<Task<BoundedLine>, CancellationToken, Task> body,
        CancellationToken sessionToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        var reading = ReadAsync(read, lifetime.Token);
        // Both actual child tasks are retained in settling and joined before
        // lifetime disposal, including body/read/cancellation fault paths.
#pragma warning disable CA2025
        var executing = ExecuteAsync(body, reading, lifetime.Token);
#pragma warning restore CA2025
        await executing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#pragma warning disable CA2025
        var cancellation = CancelAsync(lifetime);
#pragma warning restore CA2025
        var settling = Task.WhenAll(executing, reading, cancellation);
        await settling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (settling.Exception is { } inventory)
        {
            var failures = inventory.Flatten().InnerExceptions
                .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
            if (failures.Length == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException(failures);
        }
        // Read cancellation caused by leaving IDLE is expected. The body still
        // forwards its own cancellation (including the original session token).
        await executing.ConfigureAwait(false);
    }

    private static async Task<BoundedLine> ReadAsync(
        Func<CancellationToken, Task<BoundedLine>> read, CancellationToken token) =>
        await read(token).ConfigureAwait(false);

    private static async Task ExecuteAsync(
        Func<Task<BoundedLine>, CancellationToken, Task> body, Task<BoundedLine> reading,
        CancellationToken token)
    {
        // Invoking this body starts the command's actual task HERE. The read
        // passed to it is owned and independently joined by RunAsync above.
#pragma warning disable VSTHRD003
        await body(reading, token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    private static async Task CancelAsync(CancellationTokenSource lifetime) =>
        await lifetime.CancelAsync().ConfigureAwait(false);
}
