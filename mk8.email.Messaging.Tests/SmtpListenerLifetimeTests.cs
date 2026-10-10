using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using mk8.email.Configuration;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this fixture by reflection; focused outcomes verify discovery.")]
internal sealed class SmtpListenerLifetimeTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task ActualListenerStopOwnsBothHeldSessionsIncludingAssertionFailure(bool injectAssertion)
    {
        var port = SmtpListenerFixture.ReservePort();
        var injected = new AssertFailedException("Controlled assertion during actual listener shutdown.");
        await RunAsync(new SmtpConfig { ListenAddress = "127.0.0.1", Port = port }, async (fixture, deadline) =>
        {
            await fixture.Signals.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            _ = await fixture.ConnectAsync(port, deadline.Token).ConfigureAwait(false);
            await fixture.Hold.FirstEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            _ = await fixture.ConnectAsync(port, deadline.Token).ConfigureAwait(false);
            await fixture.Hold.SecondEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var cancelledWait = new CancellationTokenSource();
            await cancelledWait.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.StopAsync(cancelledWait.Token)).ConfigureAwait(false);
            var first = fixture.StopAsync(deadline.Token);
            var second = fixture.StopAsync(deadline.Token);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.IsFalse(fixture.Completing.IsCompleted);
            Assert.AreEqual(0, fixture.DisposedScopes);
            if (injectAssertion) throw injected;
            fixture.Hold.Release();
            await Task.WhenAll(first, second).ConfigureAwait(false);
            Assert.IsTrue(fixture.Completing.IsCompletedSuccessfully);
            Assert.AreEqual(2, fixture.DisposedScopes);
        }, expectedInjection: injectAssertion ? injected : null).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task ActualListenerGreetingAndQuitCompleteBeforeDependencyRetirement()
    {
        var port = SmtpListenerFixture.ReservePort();
        await RunAsync(new SmtpConfig { ListenAddress = "127.0.0.1", Port = port }, async (fixture, deadline) =>
        {
            await fixture.Signals.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var client = await fixture.ConnectAsync(port, deadline.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
            await using var writerLifetime = writer.ConfigureAwait(false);
            Assert.StartsWith("220 localhost ", await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
            await writer.WriteLineAsync("QUIT".AsMemory(), deadline.Token).ConfigureAwait(false);
            Assert.StartsWith("221 ", await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
            await fixture.StopAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(fixture.Completing.IsCompletedSuccessfully);
            Assert.AreEqual(1, fixture.DisposedScopes);
        }, hold: false).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task StopCallbackFailureStillJoinsSessionAndIsRetainedByLaterStops()
    {
        var port = SmtpListenerFixture.ReservePort();
        await RunAsync(new SmtpConfig { ListenAddress = "127.0.0.1", Port = port }, async (fixture, deadline) =>
        {
            fixture.RecipientHold.Enabled = true;
            await fixture.Signals.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var client = await NativeSmtpClient.ConnectAsync(port, deadline.Token).ConfigureAwait(false);
            await using var clientLifetime = client.ConfigureAwait(false);
            await client.GreetAsync(deadline.Token).ConfigureAwait(false);
            await client.BeginMessageAsync("controlled@example.test", deadline.Token).ConfigureAwait(false);
            await fixture.RecipientHold.Entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var first = fixture.StopAsync(deadline.Token);
            await fixture.RecipientHold.Cancelled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var second = fixture.StopAsync(deadline.Token);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.IsFalse(fixture.Completing.IsCompleted);
            Assert.AreEqual(0, fixture.DisposedScopes);
            fixture.RecipientHold.Release();
            // Retain and observe the actual two stop handles, including their shared callback fault.
#pragma warning disable VSTHRD003
            var firstFailure = await Assert.ThrowsAsync<AggregateException>(() => first).ConfigureAwait(false);
#pragma warning restore VSTHRD003
#pragma warning disable VSTHRD003
            var secondFailure = await Assert.ThrowsAsync<AggregateException>(() => second).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            Assert.AreSame(firstFailure, secondFailure);
            Assert.IsTrue(firstFailure.Flatten().InnerExceptions.All(error => ReferenceEquals(error, fixture.RecipientHold.Failure)));
            Assert.IsTrue(fixture.Completing.IsCompletedSuccessfully);
            Assert.AreEqual(1, fixture.DisposedScopes);
            fixture.RecipientHold.FailureObserved = true;
        }, hold: false).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task FailedSiblingBindCancelsAndJoinsAlreadyStartedListener()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var smtpPort = SmtpListenerFixture.ReservePort();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await RunAsync(new SmtpConfig
        {
            ListenAddress = "127.0.0.1",
            Port = smtpPort,
            EnableSubmission = true,
            SubmissionPort = occupiedPort,
        }, async (fixture, deadline) =>
        {
            await fixture.Signals.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Completing.WaitAsync(deadline.Token)).ConfigureAwait(false);
            Assert.HasCount(1, failure.Flatten().InnerExceptions);
            Assert.IsInstanceOfType<SocketException>(failure.Flatten().InnerExceptions.Single());
            Assert.AreEqual(SocketError.AddressAlreadyInUse, ((SocketException)failure.Flatten().InnerExceptions.Single()).SocketErrorCode);
            using var client = new TcpClient();
            var refused = await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, smtpPort, deadline.Token).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(SocketError.ConnectionRefused, refused.SocketErrorCode);
        }, expectedBindFailure: true).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task StopOwnsActualBaseExecutionRatherThanOverriddenInspectionProperty()
    {
        var port = SmtpListenerFixture.ReservePort();
        await RunAsync(new SmtpConfig { ListenAddress = "127.0.0.1", Port = port }, async (fixture, deadline) =>
        {
            Assert.IsNull(fixture.Server.ExecuteTask);
            await fixture.Signals.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            _ = await fixture.ConnectAsync(port, deadline.Token).ConfigureAwait(false);
            await fixture.Hold.FirstEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var cancelledWait = new CancellationTokenSource();
            await cancelledWait.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.StopAsync(cancelledWait.Token)).ConfigureAwait(false);
            Assert.IsFalse(fixture.Completing.IsCompleted);
            Assert.AreEqual(0, fixture.DisposedScopes);
            fixture.Hold.Release();
            await fixture.StopAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(fixture.Completing.IsCompletedSuccessfully);
            Assert.AreEqual(1, fixture.DisposedScopes);
        }, hideInspectionTask: true).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public Task DisabledListenersCompleteWithoutBinding() => RunAsync(
        new SmtpConfig { ListenAddress = "127.0.0.1", EnableSmtp = false }, async (fixture, deadline) =>
        {
            await fixture.Completing.WaitAsync(deadline.Token).ConfigureAwait(false);
            await fixture.StopAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(fixture.Completing.IsCompletedSuccessfully);
            Assert.IsFalse(fixture.Signals.Started.Task.IsCompleted);
            Assert.AreEqual(0, fixture.DisposedScopes);
        });

    private async Task RunAsync(SmtpConfig smtp, Func<SmtpListenerFixture, CancellationTokenSource, Task> scenario,
        bool hold = true, AssertFailedException? expectedInjection = null, bool expectedBindFailure = false, bool hideInspectionTask = false)
    {
        Assert.IsNotNull(TestContext);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        // The unconditional finally owns disposal after the actual-task join;
        // disposal errors must be aggregated with the original, not replace it.
#pragma warning disable CA2000
        var fixture = new SmtpListenerFixture(smtp, hold, hideInspectionTask);
#pragma warning restore CA2000
        Exception? failure = null;
        try { await scenario(fixture, deadline).ConfigureAwait(false); }
        // Preserve the original assertion even if independent owned-task cleanup also fails.
#pragma warning disable CA1031
        catch (Exception original) { failure = original; }
#pragma warning restore CA1031
        finally
        {
            try { await fixture.JoinAsync(deadline, failure, expectedBindFailure).ConfigureAwait(false); }
#pragma warning disable CA1031
            catch (Exception cleanup) { failure = cleanup; }
#pragma warning restore CA1031
            try { await fixture.DisposeAsync().ConfigureAwait(false); }
#pragma warning disable CA1031
            catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
#pragma warning restore CA1031
        }
        if (expectedInjection is not null)
        {
            Assert.AreSame(expectedInjection, failure);
            Assert.IsTrue(fixture.Completing.IsCompletedSuccessfully);
            Assert.AreEqual(2, fixture.DisposedScopes);
            return;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
