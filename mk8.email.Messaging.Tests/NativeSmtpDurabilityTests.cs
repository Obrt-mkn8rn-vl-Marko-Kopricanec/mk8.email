using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this fixture by reflection; focused raw outcomes verify discovery.")]
internal sealed class NativeSmtpDurabilityTests
{
    public TestContext? TestContext { get; set; }
    private const string RawMessage = "From: sender@remote.test\r\nTo: receiver@example.test\r\n"
        + "Subject: native durable input\r\nMessage-ID: <native-durable@remote.test>\r\n"
        + "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=iso-8859-1\r\n"
        + "Content-Transfer-Encoding: 8bit\r\n\r\nFirst line\r\n.leading dot\r\n\u00ff original octet\r\n";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task NativeSmtpAcceptanceWaitsForDurableQueueAndSurvivesServiceReconstruction(bool injectAssertion)
    {
        var injected = new AssertFailedException("Controlled failure while native acceptance is held.");
        AssertFailedException? retained = null;
        await RunAsync(NativeSmtpJournalMode.HoldNativeAcceptance, async (fixture, client, deadline, ownRead) =>
        {
            await SubmitAsync(client, deadline.Token).ConfigureAwait(false);
            var reading = client.ReadAsync(deadline.Token);
            ownRead(reading);
            var control = fixture.JournalControl;
            Assert.IsNotNull(control);
            await control.AcceptanceHeld.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(reading.IsCompleted);
            var queued = await AssertPendingAsync(fixture, deadline.Token).ConfigureAwait(false);
            var original = await fixture.ReadQueuedAsync(queued, deadline.Token).ConfigureAwait(false);
            Assert.EndsWith(RawMessage, original, StringComparison.Ordinal);
            var requestId = control.EnqueueRequest;
            Assert.IsNotNull(requestId);
            var exchange = await fixture.Bus.GetAsync(requestId.Value, deadline.Token).ConfigureAwait(false);
            Assert.IsNotNull(exchange);
            Assert.AreEqual("completed", exchange.State, StringComparer.Ordinal);
            Assert.IsNotNull(exchange.Response);
            if (injectAssertion)
            {
                retained = injected;
                throw injected;
            }
            control.Release();
            var accepted = await reading.ConfigureAwait(false);
            Assert.AreEqual($"250 2.0.0 Queued as {queued.Id:N}", accepted, StringComparer.Ordinal);
            await client.WriteAsync("QUIT", deadline.Token).ConfigureAwait(false);
            Assert.StartsWith("221 ", await client.ReadAsync(deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
            await fixture.Native.Completing.WaitAsync(deadline.Token).ConfigureAwait(false);
            await fixture.DeliverAfterReconstructionAsync(deadline.Token).ConfigureAwait(false);
            await AssertDeliveredAsync(fixture, queued, original, deadline.Token).ConfigureAwait(false);
        }, injected).ConfigureAwait(false);
        if (injectAssertion) Assert.AreSame(injected, retained);
    }

    [TestMethod]
    [DataRow("missing@example.test", false)]
    [DataRow("receiver@example.test", true)]
    [DataRow("external@remote.test", false)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public Task NativeSmtpRefusedRecipientsNeverReachTheRealSubmissionQueue(string recipient, bool inactive) =>
        RunAsync(NativeSmtpJournalMode.Ordinary, async (fixture, client, deadline, _) =>
        {
            if (inactive)
            {
                var context = fixture.OpenContext();
                await using var contextLifetime = context.ConfigureAwait(false);
                var inbox = await context.Inboxes.Include(item => item.Owner).SingleAsync(deadline.Token).ConfigureAwait(false);
                inbox.Owner.IsActive = false;
                await context.SaveChangesAsync(deadline.Token).ConfigureAwait(false);
            }
            await client.BeginMessageAsync(recipient, deadline.Token).ConfigureAwait(false);
            Assert.StartsWith("550 ", await client.ReadAsync(deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
            await client.WriteAsync("DATA", deadline.Token).ConfigureAwait(false);
            Assert.StartsWith("503 ", await client.ReadAsync(deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
            var verification = fixture.OpenContext();
            await using var verificationLifetime = verification.ConfigureAwait(false);
            Assert.AreEqual(0, await verification.MailQueueMessages.CountAsync(deadline.Token).ConfigureAwait(false));
            Assert.AreEqual(0, await verification.Emails.CountAsync(deadline.Token).ConfigureAwait(false));
            Assert.IsNotNull(fixture.JournalControl);
            Assert.IsNull(fixture.JournalControl.EnqueueRequest);
        });

    [TestMethod]
    [DataRow(NativeSmtpJournalMode.RefuseEnqueueInput, 0)]
    [DataRow(NativeSmtpJournalMode.RefuseEnqueueOutput, 1)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public Task NativeSmtpJournalRefusalWithholdsAcknowledgementAtItsActualCommitBoundary(NativeSmtpJournalMode mode, int expectedQueued) =>
        RunAsync(mode, async (fixture, client, deadline, _) =>
        {
            await SubmitAsync(client, deadline.Token).ConfigureAwait(false);
            Assert.StartsWith("451 ", await client.ReadAsync(deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
            Assert.IsNotNull(fixture.JournalControl);
            Assert.IsTrue(fixture.JournalControl.Refused);
            var context = fixture.OpenContext();
            await using var contextLifetime = context.ConfigureAwait(false);
            Assert.AreEqual(expectedQueued, await context.MailQueueMessages.CountAsync(deadline.Token).ConfigureAwait(false));
            Assert.AreEqual(0, await context.Emails.CountAsync(deadline.Token).ConfigureAwait(false));
            if (expectedQueued == 1)
            {
                var queued = await AssertPendingAsync(fixture, deadline.Token).ConfigureAwait(false);
                Assert.EndsWith(RawMessage, await fixture.ReadQueuedAsync(queued, deadline.Token).ConfigureAwait(false), StringComparison.Ordinal);
                var requestId = fixture.JournalControl.EnqueueRequest;
                Assert.IsNotNull(requestId);
                var committed = await fixture.Bus.GetAsync(requestId.Value, deadline.Token).ConfigureAwait(false);
                Assert.IsNotNull(committed);
                Assert.AreEqual("completed", committed.State, StringComparer.Ordinal);
                Assert.IsNotNull(committed.Response);
                var replay = await fixture.Bus.SendAsync(committed.Request, deadline.Token).ConfigureAwait(false);
                Assert.AreSequenceEqual(committed.Response.Payload, replay.Payload);
                Assert.AreEqual(1, await context.MailQueueMessages.CountAsync(deadline.Token).ConfigureAwait(false));
            }
        });

    private static async Task SubmitAsync(NativeSmtpClient client, CancellationToken cancellationToken)
    {
        await client.BeginMessageAsync("receiver@example.test", cancellationToken).ConfigureAwait(false);
        Assert.StartsWith("250 ", await client.ReadAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal);
        await client.WriteAsync("DATA", cancellationToken).ConfigureAwait(false);
        Assert.StartsWith("354 ", await client.ReadAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal);
        await client.WriteDataAsync(RawMessage, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MailQueueMessageDB> AssertPendingAsync(NativeSmtpDurabilityFixture fixture, CancellationToken cancellationToken)
    {
        var context = fixture.OpenContext();
        await using var contextLifetime = context.ConfigureAwait(false);
        var queued = await context.MailQueueMessages.AsNoTracking().Include(item => item.Recipients).SingleAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Pending, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueDirections.Inbound, queued.Direction, StringComparer.Ordinal);
        Assert.IsNull(queued.AuthenticatedUser);
        Assert.IsNull(queued.RawMessage);
        Assert.AreEqual(LargeObjectProviders.AzureBlob, queued.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.HasCount(1, queued.Recipients);
        Assert.IsTrue(queued.Recipients.Single().IsLocal);
        Assert.AreEqual(0, await context.Emails.CountAsync(cancellationToken).ConfigureAwait(false));
        return queued;
    }

    private static async Task AssertDeliveredAsync(NativeSmtpDurabilityFixture fixture, MailQueueMessageDB queued, string raw, CancellationToken cancellationToken)
    {
        var context = fixture.OpenContext();
        await using var contextLifetime = context.ConfigureAwait(false);
        var email = await context.Emails.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(NativeSmtpDurabilityFixture.FolderId, email.FolderId);
        Assert.AreEqual(queued.Recipients.Single().Id, email.QueueDeliveryId);
        Assert.AreEqual(1, email.Uid);
        Assert.IsGreaterThan(0L, email.ModSeq);
        Assert.IsNull(email.RawMessage);
        Assert.AreEqual(LargeObjectProviders.AzureBlob, email.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.AreSequenceEqual(MailWireEncoding.Instance.GetBytes(raw), await fixture.ReadMailboxAsync(email, cancellationToken).ConfigureAwait(false));
        var completed = await context.MailQueueMessages.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, completed.State, StringComparer.Ordinal);
    }

    private async Task RunAsync(NativeSmtpJournalMode mode,
        Func<NativeSmtpDurabilityFixture, NativeSmtpClient, CancellationTokenSource, Action<Task>, Task> scenario,
        Exception? expectedInjection = null)
    {
        Assert.IsNotNull(TestContext);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        NativeSmtpDurabilityFixture? fixture = null;
        NativeSmtpConnection? native = null;
        Task? completing = null;
        NativeSmtpClient? client = null;
        Task? reading = null;
        Exception? failure = null;
        try
        {
            // The unconditional finally directly disposes this resource after owned work is joined;
            // its caught disposal fault must be aggregated, not replace the original assertion.
#pragma warning disable CA2000
            fixture = await NativeSmtpDurabilityFixture.CreateAsync(mode).ConfigureAwait(false);
#pragma warning restore CA2000
            native = fixture.Native;
            completing = native.Completing;
            // The same finally owns the client, including any greeting/scenario failure.
#pragma warning disable CA2000
            client = await NativeSmtpClient.ConnectAsync(fixture.Native.Port, deadline.Token).ConfigureAwait(false);
#pragma warning restore CA2000
            await client.GreetAsync(deadline.Token).ConfigureAwait(false);
            await scenario(fixture, client, deadline, task => reading = task).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception original)
#pragma warning restore CA1031
        { failure = original; }
        finally
        {
            fixture?.JournalControl?.Release();
            failure = await ObserveConnectionCleanupAsync(native, completing, reading, deadline, failure).ConfigureAwait(false);
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
#pragma warning disable CA1031
                catch (Exception cleanup)
#pragma warning restore CA1031
                { failure = CombineFailures(failure, cleanup); }
            }
            if (fixture is not null)
            {
                try { await fixture.DisposeAsync().ConfigureAwait(false); }
#pragma warning disable CA1031
                catch (Exception cleanup)
#pragma warning restore CA1031
                { failure = CombineFailures(failure, cleanup); }
            }
        }
        if (failure is not null && !ReferenceEquals(failure, expectedInjection)) ExceptionDispatchInfo.Capture(failure).Throw();
        if (expectedInjection is not null && failure is not null) Assert.AreSame(expectedInjection, failure);
    }

    private static async Task<Exception?> ObserveConnectionCleanupAsync(NativeSmtpConnection? native, Task? completing,
        Task? reading, CancellationTokenSource deadline, Exception? failure)
    {
        if (completing is null || native is null) return failure;
        try
        {
            // These are the retained constructor-published handler and client-read handles,
            // not substitute tasks; independent cancellation and bounded observation own them.
#pragma warning disable VSTHRD003
            await GatewayEwsRouteTests.ObserveWriterCleanupAsync(completing, reading, deadline, failure,
                rollback: _ => completing.IsCompleted ? Task.CompletedTask : native.RequestStopAsync()).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            return failure;
        }
#pragma warning disable CA1031
        catch (Exception cleanup)
#pragma warning restore CA1031
        { return CombineFailures(failure, cleanup); }
    }

    private static Exception CombineFailures(Exception? original, Exception cleanup)
    {
        if (original is null || ReferenceEquals(original, cleanup)
            || (cleanup is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(original)))
        {
            return cleanup;
        }
        return new AggregateException(original, cleanup);
    }
}
