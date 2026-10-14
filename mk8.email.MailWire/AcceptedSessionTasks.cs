using System.Net.Sockets;

namespace mk8.email.MailWire;

// Publish an accepted session BEFORE its handler can make a greeting/effect
// observable. Snapshot joins do not cancel admission or the accepted work.
internal sealed class AcceptedSessionTasks
{
    private readonly Lock gate = new();
    private readonly List<Task> sessions = [];

    internal void Start(TcpClient client, Func<TcpClient, CancellationToken, Task> handle, CancellationToken token)
    {
        var body = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completing = body.Task.Unwrap();
        lock (gate)
        {
            sessions.RemoveAll(task => task.IsCompletedSuccessfully);
            sessions.Add(completing);
        }
        body.SetResult(HandleAsync(client, handle, token));
    }

    internal Task JoinAdmittedAsync()
    {
        lock (gate) return Task.WhenAll(sessions);
    }

    private static async Task HandleAsync(TcpClient client,
        Func<TcpClient, CancellationToken, Task> handle, CancellationToken token)
    {
        using var clientLifetime = client;
        await handle(client, token).ConfigureAwait(false);
    }
}
