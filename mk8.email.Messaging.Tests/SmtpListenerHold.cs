using mk8.email.Contracts.Messaging;
using mk8.email.MailWire;

namespace mk8.email.Messaging.Tests;

internal sealed class SmtpListenerHold : IGatewayTrafficJournal
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;
    public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Enabled { get; init; } = true;

    public async Task AppendAsync(GatewayTrafficRecord record, CancellationToken cancellationToken = default)
    {
        if (!Enabled || !string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal)
            || !MailWireEncoding.Instance.GetString(record.Payload).StartsWith("220 ", StringComparison.Ordinal))
        {
            return;
        }
        var entered = Interlocked.Increment(ref _entered);
        if (entered == 1) FirstEntered.TrySetResult();
        if (entered == 2) SecondEntered.TrySetResult();
        // Deliberately hold actual handler work beyond stop cancellation. The
        // fixture owns release; cancellation is not evidence of task settlement.
        // This fixture-owned gate deliberately overlaps stop; finally always releases it.
#pragma warning disable VSTHRD003
        await _release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This listener control does not store or replay traffic.");

    public void Release() => _release.TrySetResult();
}
