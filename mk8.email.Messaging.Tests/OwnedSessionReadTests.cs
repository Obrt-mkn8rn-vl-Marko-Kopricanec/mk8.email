using mk8.email.MailWire;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest reflects these finite actual read-owner controls.")]
internal sealed class OwnedSessionReadTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task BodyExitCancelsAndActuallyJoinsHeldReadWithIndependentFaults(bool bodyFault, bool readFault)
    {
        Assert.IsNotNull(TestContext);
        var held = new HeldRead();
        var bodyError = new IOException("Controlled IDLE response/poll failure.");
        var readError = new InvalidOperationException("Controlled late journal failure.");
        var bodyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completing = OwnedSessionRead.RunAsync(held.ReadAsync, async (_, _) =>
        {
            bodyEntered.TrySetResult();
            // The outer finally always releases and joins this finite body.
#pragma warning disable VSTHRD003
            await bodyRelease.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            if (bodyFault) throw bodyError;
        }, TestContext.CancellationToken);
        try
        {
            await bodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            bodyRelease.TrySetResult();
            await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(completing.IsCompleted, "Cancellation cannot retire a still-held actual read/journal body.");
            if (readFault) held.Failure = readError;
            held.Release.TrySetResult();
        }
        finally
        {
            bodyRelease.TrySetResult();
            if (readFault) held.Failure = readError;
            held.Release.TrySetResult();
            await completing.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.IsTrue(completing.IsCompleted, "Unsettled work forbids dependency retirement.");
        }
        Assert.IsTrue(held.Settled);
        if (bodyFault || readFault)
        {
            Assert.IsNotNull(completing.Exception);
            var failures = completing.Exception.Flatten().InnerExceptions;
            Assert.HasCount((bodyFault ? 1 : 0) + (readFault ? 1 : 0), failures);
            if (bodyFault) Assert.Contains(bodyError, failures);
            if (readFault) Assert.Contains(readError, failures);
        }
        else
        {
            Assert.IsTrue(completing.IsCompletedSuccessfully);
        }
    }

    private sealed class HeldRead
    {
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure { get; set; }
        internal bool Settled { get; private set; }

        internal async Task<BoundedLine> ReadAsync(CancellationToken token)
        {
            var registration = token.Register(() => Cancelled.TrySetResult());
            await using var registrationLifetime = registration.ConfigureAwait(false);
            try
            {
                // The outer test releases and joins this read independently of cancellation.
#pragma warning disable VSTHRD003
                await Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                if (Failure is { } error) throw error;
                return new BoundedLine("DONE", IsTooLong: false);
            }
            finally { Settled = true; }
        }
    }
}
