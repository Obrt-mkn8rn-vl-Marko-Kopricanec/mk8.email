namespace mk8.email.Messaging.Tests;

internal sealed class SmtpListenerRecipientHold
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InvalidOperationException Failure { get; } = new("Controlled session cancellation callback fault.");
    public bool Enabled { get; set; }
    public bool FailureObserved { get; set; }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) throw new NotSupportedException("This listener control does not admit recipients.");
        var registration = cancellationToken.Register(() =>
        {
            Cancelled.TrySetResult();
            throw Failure;
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
}
