using System.Net.Sockets;

namespace mk8.email.MailWire;

// Each listener owns its accepted work. Shared lifetime cancellation closes
// sibling admission, but hosted completion waits for actual session settlement.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0182", Justification = "IMAP and POP3 friend assemblies invoke this shared internal owner; actual native consumer controls execute it.")]
internal static class OwnedTcpListener
{
    internal static async Task RunAsync(TcpListener listener, CancellationTokenSource lifetime,
        Func<TcpClient, CancellationToken, Task> handle, Action started, Action stopped)
    {
        var sessions = new List<Task>();
        var accepting = AcceptAsync(listener, handle, started, sessions, lifetime.Token);
        await accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var completing = Task.WhenAll(accepting, CloseAsync(listener, stopped),
            lifetime.CancelAsync(), Task.WhenAll(sessions));
        await completing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (completing.Exception is { } faults) throw faults;
        await completing.ConfigureAwait(false);
    }

    private static async Task AcceptAsync(TcpListener listener,
        Func<TcpClient, CancellationToken, Task> handle, Action started, List<Task> sessions, CancellationToken token)
    {
        try
        {
            listener.Start();
            started();
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                // The async admission wrapper retains a synchronous handler fault
                // too, and keeps the accepted socket alive until its task settles.
                sessions.Add(HandleAsync(client, handle, token));
                sessions.RemoveAll(task => task.IsCompletedSuccessfully);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task HandleAsync(TcpClient client,
        Func<TcpClient, CancellationToken, Task> handle, CancellationToken token)
    {
        using var clientLifetime = client;
        await handle(client, token).ConfigureAwait(false);
    }

    private static async Task CloseAsync(TcpListener listener, Action stopped)
    {
        // Publish even synchronous close/log faults as an owned task so neither
        // can skip the cancellation/session fault inventory above.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        listener.Stop();
        stopped();
    }
}
