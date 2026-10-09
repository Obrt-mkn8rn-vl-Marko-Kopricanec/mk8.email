using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EntryCleanupOwnsCompletionBeforeItsInvocationReturnsOrLinksTheBody(bool committed)
    {
        using var cancellation = new CancellationTokenSource();
        var original = new AssertFailedException("Controlled original entry assertion.");
        var fatal = new InvalidOperationException("Controlled completion failure before invocation return.");
        var inner = new CompletionPublicationConsumer();
        Task? cleanup = null;
        Task? entryHandle = null;
        var invocationReturned = false;
        ResponseCompletionBarrier? barrier = null;
        barrier = new ResponseCompletionBarrier(inner, committed, () =>
        {
            Assert.IsNotNull(barrier);
            Assert.IsFalse(invocationReturned);
            Assert.IsFalse(barrier.BodyLinked);
            Assert.IsTrue(barrier.Entered.Task.IsCompletedSuccessfully);
            Assert.IsFalse(barrier.Completing.IsCompleted);
            entryHandle = barrier.Completing;
            barrier.Release.TrySetResult();
            // Start the real owned cleanup while the body has not returned/published its Task yet.
            // The callback-created task is retained and joined with invocation before cancellation/fixture disposal.
#pragma warning disable CA2025
            cleanup = ObserveWriterCleanupAsync(Task.CompletedTask, entryHandle, cancellation, original);
#pragma warning restore CA2025
            Assert.IsFalse(cleanup.IsCompleted);
            throw fatal;
        });
        var published = barrier.Completing;
        var (lease, response) = CompletionPublicationValues();
        if (committed) await barrier.CompleteAsync(lease, response).ConfigureAwait(false);
        var invocation = barrier.CompleteAsync(lease, response);
        invocationReturned = true;
        try
        {
            // Both tasks were started by this control; the callback-held cleanup is joined even on a fault.
#pragma warning disable VSTHRD003
            await Task.WhenAll(invocation, cleanup ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, fatal)) { }
        catch (AggregateException) { }
        Assert.IsNotNull(cleanup);
        Assert.AreSame(published, entryHandle);
        Assert.AreSame(published, invocation);
        Assert.IsTrue(barrier.BodyLinked);
        Assert.IsTrue(invocation.IsFaulted);
        Assert.AreSame(fatal, invocation.Exception!.InnerExceptions.Single());
        Assert.IsTrue(cleanup.IsFaulted);
        CollectionAssert.AreEqual(new Exception[] { original, fatal }, cleanup.Exception!.Flatten().InnerExceptions.ToArray());
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(committed ? 2 : 0, inner.Completions);
    }

    private static (ApplicationRequestLease Lease, ApplicationResponse Response) CompletionPublicationValues()
    {
        var request = new ApplicationRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "ews",
            ApplicationOperations.MailOperationExecute, "application/json", [], new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        return (new ApplicationRequestLease(request, "controlled-worker", DateTimeOffset.UtcNow.AddMinutes(1), 1),
            new ApplicationResponse(request.Id, "application/json", [], new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    private sealed class CompletionPublicationConsumer : IApplicationRequestConsumer
    {
        public int Completions { get; private set; }

        public Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(lease.Request.Id, response.RequestId);
            Assert.AreEqual(CancellationToken.None, cancellationToken);
            Completions++;
            return Task.CompletedTask;
        }

        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
