namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes these finite health-observation ownership controls by reflection.")]
internal sealed class NativeHealthObservationTests
{
    private static readonly NativeHealthPoint Point = new(1, 2, 3, 4, 5, 6, 7, 8);

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
