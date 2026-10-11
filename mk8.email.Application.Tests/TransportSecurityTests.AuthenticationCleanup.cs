using System.Runtime.ExceptionServices;
using mk8.email.Application.Interfaces;

namespace mk8.email.Application.Tests;

internal sealed partial class TransportSecurityTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task AuthenticationCleanupTimeoutRetainsActualBodyAndLaterOutcome(bool injectAssertion, bool faultBody)
    {
        Assert.IsNotNull(TestContext);
        var innerBody = new TaskCompletionSource<AuthenticatedMailUser?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new HeldNativeAuthentication(afterInner: false);
        // This control creates, releases and joins the finite body through the immutable held proxy.
#pragma warning disable VSTHRD003
        var authenticator = new ControlledMailAuthenticator(() =>
        {
            innerEntered.TrySetResult();
            return innerBody.Task;
        });
#pragma warning restore VSTHRD003
        var retained = held.InvokeAsync(authenticator, primary: false, "reserved-user", "reserved-password", CancellationToken.None);
        AssertFailedException? original = null;
        var bodyFault = new InvalidOperationException("finite authentication body fault after timeout");
        Exception? failure = null;
        Exception? timedOut = null;
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
            if (injectAssertion)
                original = Assert.Throws<AssertFailedException>(() => Assert.Fail("finite original assertion"));
            held.ReleaseAndSeal();
            await innerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
            timedOut = await ObserveOwnedAuthenticationCleanupAsync(retained, original, TimeSpan.Zero).ConfigureAwait(false);
            AssertCleanupTimeout(original, timedOut);
            Assert.IsFalse(retained.IsCompleted, "A cleanup observation timeout must not claim actual body settlement.");
            Assert.IsFalse(innerBody.Task.IsCompleted);
            Assert.AreSame(retained, held.Completing);
        }
#pragma warning disable CA1031 // Keep an unexpected control failure while independently settling the finite actual authentication body.
        catch (Exception error) when (!IsFatalAuthenticationControl(error))
        {
            failure = error;
        }
#pragma warning restore CA1031
        finally
        {
            held.ReleaseAndSeal();
            if (faultBody)
                innerBody.TrySetException(bodyFault);
            else
                innerBody.TrySetResult(null);
            var observed = await ObserveOwnedAuthenticationCleanupAsync(retained, failure ?? timedOut).ConfigureAwait(false);
            if (failure is not null)
                failure = observed;
            else
                timedOut = observed;
        }
        if (!retained.IsCompleted)
            throw new InvalidOperationException("Actual authentication remains active; dependency retirement refused.", failure ?? timedOut);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        AssertSettledAuthenticationOutcome(faultBody, bodyFault, original, timedOut, retained);
    }

    private static void AssertCleanupTimeout(Exception? original, Exception? outcome)
    {
        if (original is null)
        {
            Assert.IsInstanceOfType<TimeoutException>(outcome);
            return;
        }
        var inventory = outcome as AggregateException;
        Assert.IsNotNull(inventory);
        Assert.HasCount(2, inventory.InnerExceptions);
        Assert.AreSame(original, inventory.InnerExceptions[0]);
        Assert.IsInstanceOfType<TimeoutException>(inventory.InnerExceptions[1]);
    }

    private static void AssertSettledAuthenticationOutcome(
        bool faultBody, Exception bodyFault, Exception? original, Exception? outcome, Task retained)
    {
        if (!faultBody)
        {
            Assert.IsTrue(retained.IsCompletedSuccessfully);
            AssertCleanupTimeout(original, outcome);
            return;
        }
        Assert.IsTrue(retained.IsFaulted);
        var inventory = outcome as AggregateException;
        Assert.IsNotNull(inventory);
        Assert.HasCount(2, inventory.InnerExceptions);
        AssertCleanupTimeout(original, inventory.InnerExceptions[0]);
        Assert.AreSame(bodyFault, inventory.InnerExceptions[1]);
    }
}
