using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ResponseCompletionPausesKeepQueueVisibilityAndOwnedCleanupDistinct(bool committed, bool assertionFailure)
    {
        ResponseCompletionBarrier? barrier = null;
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true,
            decorateConsumer: inner => barrier = new ResponseCompletionBarrier(inner, committed)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        Assert.IsNotNull(barrier);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
        var original = assertionFailure ? new AssertFailedException("Controlled response completion observation failure.") : null;
        if (original is null) await RunResponseCompletionControlAsync(fixture, barrier).ConfigureAwait(false);
        else Assert.AreSame(original, await Assert.ThrowsAsync<AssertFailedException>(() =>
            RunResponseCompletionControlAsync(fixture, barrier, original)).ConfigureAwait(false));
        Assert.IsNotNull(barrier.Completing);
        Assert.IsTrue(barrier.Completing.IsCompletedSuccessfully);
        Assert.IsTrue(barrier.SendingCompleted);
    }

    private static async Task RunResponseCompletionControlAsync(CaptureFixture fixture, ResponseCompletionBarrier barrier,
        AssertFailedException? injectedFailure = null)
    {
        using var request = ResponseCompletionRequest();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Owned HTTP task is directly awaited and unconditionally joined before request/fixture disposal.
#pragma warning disable CA2025
        var sending = fixture.Client.SendAsync(request, cancellation.Token);
#pragma warning restore CA2025
        Exception? originalFailure = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (injectedFailure is not null) throw injectedFailure;
            if (barrier.Committed)
            {
                using var response = await sending.ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                AssertCode(XDocument.Parse(body), "NoError");
                await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
                Assert.IsNull(fixture.Diagnostics.LastReport);
                using var empty = JsonDocument.Parse("{}");
                AssertResponseCompletionPoint(fixture.Diagnostics.Report(empty.RootElement, "Running"), barrier, delivered: true);
            }
            else
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                var cancelled = false;
                try
                {
                    using var response = await sending.ConfigureAwait(false);
                    Assert.Fail("The controlled pre-completion request must cancel.");
                }
                catch (OperationCanceledException) { cancelled = true; }
                Assert.IsTrue(cancelled);
                Assert.IsNotNull(fixture.Diagnostics.LastReport);
                AssertResponseCompletionPoint(fixture.Diagnostics.LastReport, barrier, delivered: false);
            }
        }
        // Original assertion is retained if the independently owned completion task also faults during cleanup.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { originalFailure = exception; throw; }
        finally
        {
            barrier.Release.TrySetResult();
            // Join the exact completion task retained by the controlled consumer, not unrelated external work.
#pragma warning disable VSTHRD003
            await ObserveWriterCleanupAsync(sending, barrier.Completing, cancellation, originalFailure).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            barrier.SendingCompleted = sending.IsCompleted;
            if (sending.IsCompletedSuccessfully)
            {
                // Observe/dispose a possible success even on the injected assertion-failure branch.
#pragma warning disable VSTHRD003
                using var response = await sending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
        }
    }

    private static HttpRequestMessage ResponseCompletionRequest() => new(HttpMethod.Post, CanonicalPath)
    {
        Content = XmlContent(Request("GetFolder", $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>")),
    };

    private static void AssertResponseCompletionPoint(string text, ResponseCompletionBarrier barrier, bool delivered)
    {
        using var report = JsonDocument.Parse(text);
        var events = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var started = events.Last(value => value.GetProperty("Activity").GetString() is "ResponseComplete"
            && value.GetProperty("Phase").GetString() is "TransportStart");
        Assert.AreEqual(barrier.RequestId, started.GetProperty("Request").GetGuid());
        Assert.AreEqual("Mail", started.GetProperty("Operation").GetString(), StringComparer.Ordinal);
        Assert.IsTrue(events.Any(value => value.GetProperty("Request").ValueKind is JsonValueKind.String
            && value.GetProperty("Request").GetGuid() == barrier.RequestId && value.GetProperty("Phase").GetString() is "DispatchComplete"));
        var span = started.GetProperty("Span").GetGuid();
        Assert.IsFalse(events.Any(value => value.GetProperty("Span").ValueKind is JsonValueKind.String
            && value.GetProperty("Span").GetGuid() == span && value.GetProperty("Phase").GetString() is "TransportReturned"));
        Assert.AreEqual(delivered, events.Any(value => value.GetProperty("Request").ValueKind is JsonValueKind.String
            && value.GetProperty("Request").GetGuid() == barrier.RequestId && value.GetProperty("Activity").GetString() is "ClientBusSend"
            && value.GetProperty("Phase").GetString() is "TransportReturned"));
        if (!delivered)
        {
            Assert.AreEqual(1, report.RootElement.GetProperty("database").GetProperty("queue").GetProperty("processing").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("database").GetProperty("journal").GetProperty("presentationOutbound").GetInt32());
        }
    }

    private sealed class ResponseCompletionBarrier(IApplicationRequestConsumer inner, bool committed) : IApplicationRequestConsumer
    {
        // GetFolder performs count then read; post-commit delivery requires pausing the final read completion.
        private int _remaining = committed ? 2 : 1;
        public bool Committed => committed;
        public Guid RequestId { get; private set; }
        public Task? Completing { get; private set; }
        public bool SendingCompleted { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response, CancellationToken cancellationToken = default)
        {
            if (lease.Request.Operation is not ApplicationOperations.MailOperationExecute || Interlocked.Decrement(ref _remaining) != 0)
                return inner.CompleteAsync(lease, response, cancellationToken);
            RequestId = lease.Request.Id;
            Completing = CompletePausedAsync(lease, response, cancellationToken);
            return Completing;
        }

        private async Task CompletePausedAsync(ApplicationRequestLease lease, ApplicationResponse response, CancellationToken token)
        {
            if (!committed) { Entered.TrySetResult(); await Release.Task.WaitAsync(token).ConfigureAwait(false); }
            await inner.CompleteAsync(lease, response, token).ConfigureAwait(false);
            if (committed) { Entered.TrySetResult(); await Release.Task.WaitAsync(token).ConfigureAwait(false); }
        }

        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default) =>
            inner.WaitForRequestAsync(workerId, cancellationToken);
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) =>
            inner.TryClaimAsync(workerId, cancellationToken);
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default) =>
            inner.RenewLeaseAsync(lease, cancellationToken);
        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail, CancellationToken cancellationToken = default) =>
            inner.FailAsync(lease, errorCode, errorDetail, cancellationToken);
    }
}
