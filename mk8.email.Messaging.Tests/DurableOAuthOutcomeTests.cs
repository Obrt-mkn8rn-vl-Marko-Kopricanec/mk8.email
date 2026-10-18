using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest reflects these operation-bound body/disposal fault controls.")]
internal sealed class DurableOAuthOutcomeTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task BodyFailureWaitsForActualRetirementAndRetainsReceiptRecoveryIdentity(bool recoverable, bool disposalFault)
    {
        Assert.IsNotNull(TestContext);
        var operation = Guid.CreateVersion7();
        var original = new IOException("Controlled ordinary body/commit observation failure.");
        var disposal = new InvalidOperationException("Controlled actual resource retirement failure.");
#pragma warning disable CA2000 // OwnAsync immediately takes ownership; the finally below releases and joins its actual one-time disposal.
        var resource = new HeldResource(disposalFault ? disposal : null);
#pragma warning restore CA2000
        var outcome = new DurableOAuthOutcome(operation);
        if (recoverable) outcome.RequireReceiptRecovery();
        var completing = outcome.OwnAsync(resource, () => Task.FromException<OAuthTokenApplicationResult>(original));
        try
        {
            await resource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(completing.IsCompleted);
            Assert.IsFalse(resource.Settled);
        }
        finally
        {
            resource.Release.TrySetResult();
            await ((Task)completing).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.IsTrue(completing.IsCompleted, "An observation timeout is failed verification, not actual retirement.");
        }
        Assert.IsTrue(resource.Settled);
        Assert.AreEqual(1, resource.Disposals);
        Assert.IsNotNull(completing.Exception);
        var errors = completing.Exception.Flatten().InnerExceptions;
        if (recoverable)
        {
            Assert.HasCount(1, errors);
            var recovery = errors[0] as DurableOAuthRecoveryRequiredException;
            Assert.IsNotNull(recovery);
            Assert.AreEqual(operation, recovery.OperationId);
            Assert.IsNotNull(recovery.InnerException);
            errors = recovery.InnerException is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions : new AggregateException(recovery.InnerException).InnerExceptions;
        }
        Assert.Contains(original, errors);
        if (disposalFault) Assert.Contains(disposal, errors);
        Assert.HasCount(disposalFault ? 2 : 1, errors);
    }

    private sealed class HeldResource(Exception? failure) : IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Settled { get; private set; }
        internal int Disposals { get; private set; }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            Entered.TrySetResult();
            try
            {
                // Outer control always releases and independently joins this actual retirement.
#pragma warning disable VSTHRD003
                await Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                if (failure is not null) throw failure;
            }
            finally { Settled = true; }
        }
    }
}
