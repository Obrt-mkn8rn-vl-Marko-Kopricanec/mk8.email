using System.Net.Sockets;

namespace mk8.email.MailWire;

// Each listener owns its accepted work. Shared lifetime cancellation closes
// sibling admission, but hosted completion waits for actual session settlement.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0182", Justification = "IMAP and POP3 friend assemblies invoke this shared internal owner; actual native consumer controls execute it.")]
internal static class OwnedTcpListener
{
    internal static async Task RunAsync(TcpListener listener, CancellationTokenSource lifetime,
        Func<TcpClient, CancellationToken, Task> handle, Action started, Action stopped,
        AcceptedSessionTasks? admittedSessions = null)
    {
        var sessions = admittedSessions ?? new AcceptedSessionTasks();
        var accepting = AcceptAsync(listener, handle, started, sessions, lifetime.Token);
        await accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var completing = Task.WhenAll(accepting, CloseAsync(listener, stopped),
            lifetime.CancelAsync(), sessions.JoinAdmittedAsync());
        await completing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (completing.Exception is { } faults) throw faults;
        await completing.ConfigureAwait(false);
    }

    private static async Task AcceptAsync(TcpListener listener,
        Func<TcpClient, CancellationToken, Task> handle, Action started, AcceptedSessionTasks sessions, CancellationToken token)
    {
        try
        {
            listener.Start();
            started();
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                sessions.Start(client, handle, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
