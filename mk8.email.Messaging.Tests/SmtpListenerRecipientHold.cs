namespace mk8.email.Messaging.Tests;

internal sealed class SmtpListenerRecipientHold : IDisposable
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _callbackRelease = new(initialState: false);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InvalidOperationException Failure { get; } = new("Controlled session cancellation callback fault.");
    public bool Enabled { get; set; }
    public bool HoldCancellation { get; set; }
    public bool FailureObserved { get; set; }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) throw new NotSupportedException("This listener control does not admit recipients.");
        var registration = cancellationToken.Register(() =>
        {
            Cancelled.TrySetResult();
            try
            {
                // A finite synchronous callback, not the separately held async
                // recipient body. Cleanup independently releases both gates.
                if (HoldCancellation && !_callbackRelease.Wait(TimeSpan.FromSeconds(15), CancellationToken.None))
                    throw new TimeoutException("Controlled synchronous callback was not released.");
                throw Failure;
            }
            finally { CallbackCompleted.TrySetResult(); }
        });
        await using var registrationLifetime = registration.ConfigureAwait(false);
        Entered.TrySetResult();
        // The actual handler owns this body; fixture cleanup releases before independently joining it.
#pragma warning disable VSTHRD003
        await _release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        return true;
    }

    public void Release() => _release.TrySetResult();
    public void ReleaseCancellation() => _callbackRelease.Set();
    public void Dispose() => _callbackRelease.Dispose();
}
