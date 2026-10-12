namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes these finite health-observation ownership controls by reflection.")]
internal sealed class NativeHealthObservationTests
{
    private static readonly NativeHealthPoint Point = new(1, 2, 3, 4, 5, 6, 7, 8);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulHealthResponseIsDisposedDespiteSettlementFault(bool disposalFault)
    {
        var original = new AssertFailedException("Controlled retained assertion.");
        var cleanup = new InvalidOperationException("Controlled settlement fault after request return.");
        var disposal = disposalFault ? new IOException("Controlled content disposal fault.") : null;
        var stream = new DisposalStream(disposal);
        await using var streamLifetime = stream.ConfigureAwait(false);
        using var response = new DisposalResponse { Content = new StreamContent(stream) };
        // FromResult is already successfully completed, not work using this
        // resource later. Explicit observation and actual helper disposal follow.
#pragma warning disable CA2025
        var sending = Task.FromResult<HttpResponseMessage>(response);
#pragma warning restore CA2025
        Assert.AreSame(response, await sending.ConfigureAwait(false));
        var settlement = new AggregateException(original, cleanup);
        AggregateException? observed = null;
        try
        {
            await NativeHostStartupTests.SettleAndDisposeHealthResponseAsync(sending,
                Task.FromException(settlement), original).ConfigureAwait(false);
            Assert.Fail("The actual ordinary settlement fault must survive response disposal.");
        }
        catch (AggregateException error) { observed = error; }
        Assert.IsNotNull(observed);
        Assert.IsTrue(response.DisposalEntered);
        Assert.AreEqual(!disposalFault, response.DisposalCompleted);
        Assert.IsFalse(stream.CanRead);
        if (disposalFault)
        {
            var readError = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                response.Content.ReadAsStringAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(typeof(ObjectDisposedException), readError.InnerException?.GetType());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
                response.Content.ReadAsStringAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        var faults = observed.Flatten().InnerExceptions;
        Assert.HasCount(disposalFault ? 3 : 2, faults);
        Assert.Contains(original, faults);
        Assert.Contains(cleanup, faults);
        if (disposal is not null) Assert.Contains(disposal, faults);
        else Assert.AreSame(settlement, observed);
    }

    private sealed class DisposalResponse : HttpResponseMessage
    {
        internal bool DisposalEntered { get; private set; }
        internal bool DisposalCompleted { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposalEntered = true;
            base.Dispose(disposing);
            if (disposing) DisposalCompleted = true;
        }
    }

    private sealed class DisposalStream(Exception? failure) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            var wasReadable = CanRead;
            base.Dispose(disposing);
            if (disposing && wasReadable && failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [TestMethod]
    public async Task ActualBodyInventoryRetainsAllDistinctFaultsFromOneJoinedBody()
    {
        var original = new AssertFailedException("Controlled primary assertion.");
        var first = new InvalidOperationException("Controlled first body fault.");
        var second = new IOException("Controlled second body fault.");
        var body = Task.WhenAll(Task.FromException(original), Task.FromException(first), Task.FromException(second));
        var errors = new List<Exception>();
        await NativeHostStartupTests.ObserveHealthBodiesAsync([body], original, errors).ConfigureAwait(false);
        Assert.IsTrue(body.IsCompleted);
        Assert.HasCount(2, errors);
        Assert.IsTrue(errors.Exists(error => ReferenceEquals(error, first)));
        Assert.IsTrue(errors.Exists(error => ReferenceEquals(error, second)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualBodyInventoryExcludesOnlyTheOriginalAndRetainsDistinctFault(bool cancelled)
    {
        Exception original = cancelled ? new OperationCanceledException("Controlled primary cancellation.")
            : new AssertFailedException("Controlled primary assertion.");
        var primary = ThrowBodyAsync(original);
        var distinct = new InvalidOperationException("Controlled distinct body fault.");
        var secondary = Task.FromException(distinct);
        var errors = new List<Exception>();
        await NativeHostStartupTests.ObserveHealthBodiesAsync([primary, secondary], original, errors).ConfigureAwait(false);
        Assert.IsTrue(primary.IsCompleted);
        Assert.IsTrue(secondary.IsCompleted);
        Assert.HasCount(1, errors);
        Assert.AreSame(distinct, errors[0]);
    }

    private static async Task ThrowBodyAsync(Exception original)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        throw original;
    }

    [TestMethod]
    public async Task SuccessfulRequestReturnsOriginalResponseAndDoesNotCollect()
    {
        using var caller = new CancellationTokenSource();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        var observed = await NativeHealthObservation.ObserveRequestAsync(token =>
        {
            Assert.AreEqual(caller.Token, token);
            return Task.FromResult(response);
        }, _ => throw new AssertFailedException("Unexpected collection."),
            _ => Assert.Fail("Unexpected emission."), caller.Token).ConfigureAwait(false);
        Assert.AreSame(response, observed);
    }

    [TestMethod]
    public async Task OrdinaryRequestFaultIsNotConvertedOrCollected()
    {
        var original = new InvalidOperationException("Controlled original request fault.");
        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            NativeHealthObservation.ObserveRequestAsync(_ => Task.FromException<HttpResponseMessage>(original),
                _ => throw new AssertFailedException("Unexpected collection."),
                _ => Assert.Fail("Unexpected emission."), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(original, observed);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CancellationRetainsOriginalThroughIndependentCollectorAndSinkFaults(bool collectorFault, bool sinkFault)
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync().ConfigureAwait(false);
        var original = new OperationCanceledException("Controlled original cancellation.", caller.Token);
        var emits = 0;
        CancellationToken? diagnosticToken = null;
        NativeHealthPoint? emitted = null;
        var observed = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            NativeHealthObservation.ObserveRequestAsync(token =>
            {
                Assert.AreEqual(caller.Token, token);
                return Task.FromException<HttpResponseMessage>(original);
            }, token =>
            {
                diagnosticToken = token;
                return collectorFault ? Task.FromException<NativeHealthPoint>(new InvalidOperationException("Controlled collector fault."))
                    : Task.FromResult(Point);
            }, point =>
            {
                emitted = point;
                emits++;
                if (sinkFault) throw new InvalidOperationException("Controlled sink fault.");
            }, caller.Token)).ConfigureAwait(false);
        Assert.AreSame(original, observed);
        Assert.IsNotNull(diagnosticToken);
        Assert.AreNotEqual(caller.Token, diagnosticToken.Value);
        Assert.IsTrue(diagnosticToken.Value.CanBeCanceled);
        Assert.AreEqual(collectorFault ? 0 : 1, emits);
        if (!collectorFault) Assert.AreSame(Point, emitted);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CooperativeDiagnosticCancellationSettlesBeforeOriginalIsRethrown()
    {
        var original = new OperationCanceledException("Controlled request cancellation.");
        var settled = false;
        var observed = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            NativeHealthObservation.ObserveRequestAsync(_ => Task.FromException<HttpResponseMessage>(original),
                async token =>
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                    finally { settled = true; }
                    return Point;
                }, _ => Assert.Fail("Cancelled collection cannot emit."), CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(settled);
        Assert.AreSame(original, observed);
    }
}
