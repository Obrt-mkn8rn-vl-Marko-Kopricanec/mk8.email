using System.Net;
using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this fixture; new diagnostic outcomes are retained and counted.")]
internal sealed class GatewayFixtureDiagnosticsTests
{
    [TestMethod]
    public void EventHistoryIsBoundedAndRetainsOnlyRegisteredPhaseOperationAndOpaqueId()
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        var id = Guid.CreateVersion7();
        for (var index = 0; index < GatewayFixtureDiagnostics.MaximumEvents + 20; index++)
            diagnostics.Record(GatewayFixtureDiagnostics.Phase.DispatchStart, id,
                GatewayFixtureDiagnostics.Classify("UNTRUSTED SECRET OPERATION"));
        using var database = JsonDocument.Parse("{}");
        using var report = JsonDocument.Parse(diagnostics.Report(database.RootElement, "Running"));
        Assert.AreEqual(20, report.RootElement.GetProperty("droppedEvents").GetInt32());
        Assert.AreEqual(GatewayFixtureDiagnostics.MaximumEvents, report.RootElement.GetProperty("events").GetArrayLength());
        foreach (var phase in report.RootElement.GetProperty("events").EnumerateArray())
        {
            Assert.AreEqual("DispatchStart", phase.GetProperty("Phase").GetString(), StringComparer.Ordinal);
            Assert.AreEqual("Other", phase.GetProperty("Operation").GetString(), StringComparer.Ordinal);
            Assert.AreEqual(id, phase.GetProperty("Request").GetGuid());
        }
        Assert.IsFalse(report.RootElement.GetRawText().Contains("UNTRUSTED SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(ApplicationOperations.JmapProfileGet, "Profile")]
    [DataRow(ApplicationOperations.MailOperationExecute, "Mail")]
    [DataRow(ApplicationOperations.ImapAuthenticatePassword, "Authenticate")]
    [DataRow("other-secret-input", "Other")]
    public void OperationNamesAreAClosedDiagnosticProjection(string operation, string expected) =>
        Assert.AreEqual(expected, GatewayFixtureDiagnostics.Classify(operation).ToString(), StringComparer.Ordinal);

    [TestMethod]
    public async Task SuccessfulHttpResponseDoesNotCollectOrPublishAnyFailureSnapshot()
    {
        using var inner = new ControlledHandler();
        var diagnostics = new GatewayFixtureDiagnostics();
        using var handler = new GatewayFixtureFailureHandler(inner, diagnostics,
            _ => throw new AssertFailedException("Successful response must not collect diagnostics."),
            _ => throw new AssertFailedException("Successful response must not publish diagnostics."));
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://secret-host.example.test/secret-path?token=SECRET");
        using var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, inner.Calls);
        Assert.IsNull(diagnostics.LastReport);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationPreservesOriginalExceptionEvenWhenSnapshotOrSinkFails(bool sink)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var original = new OperationCanceledException("ORIGINAL SECRET TRANSPORT FAILURE", cancellation.Token);
        using var inner = new ControlledHandler(original);
        var diagnostics = new GatewayFixtureDiagnostics();
        string? report = null;
        using var handler = new GatewayFixtureFailureHandler(inner, diagnostics,
            _ => throw new InvalidOperationException("SECONDARY SECRET SNAPSHOT FAILURE"),
            value => { report = value; if (sink) throw new InvalidOperationException("SECONDARY SECRET SINK FAILURE"); });
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://secret-host.example.test/secret-path?token=SECRET");
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token)).ConfigureAwait(false);
        Assert.AreSame(original, failure);
        Assert.AreEqual(1, inner.Calls);
        Assert.IsNotNull(report);
        StringAssert.Contains(report, "InvalidOperationException", StringComparison.Ordinal);
        Assert.IsFalse(report.Contains("SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DiagnosticCollectionUsesItsOwnFiniteBudgetAndJoinsTheCancelledCollector()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var original = new OperationCanceledException(cancellation.Token);
        var diagnostics = new GatewayFixtureDiagnostics();
        var observed = false;
        string? report = null;
        using var inner = new ControlledHandler(original);
        using var handler = new GatewayFixtureFailureHandler(inner, diagnostics,
            async token =>
            {
                Assert.AreNotEqual(cancellation.Token, token);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                finally { observed = token.IsCancellationRequested; }
                return "unreachable";
            }, value => report = value);
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test");
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token)).ConfigureAwait(false);
        Assert.AreSame(original, failure);
        Assert.IsTrue(observed);
        Assert.IsNotNull(report);
        StringAssert.Contains(report, "TaskCanceledException", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DispatchPhasesCorrelateTheOriginalIdWithoutPayloadOrErrorDisclosure(bool fail)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        var original = new InvalidOperationException("SECRET DISPATCH FAILURE");
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new ControlledDispatcher(fail ? original : null), diagnostics);
        var request = new ApplicationRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "ews", ApplicationOperations.MailOperationExecute,
            "application/json", "SECRET PAYLOAD"u8.ToArray(), new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        if (fail)
            Assert.AreSame(original, await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(request)).ConfigureAwait(false));
        else Assert.AreEqual(request.Id, (await dispatcher.DispatchAsync(request).ConfigureAwait(false)).RequestId);
        using var database = JsonDocument.Parse("{}");
        using var report = JsonDocument.Parse(diagnostics.Report(database.RootElement, "Running"));
        var phases = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.HasCount(2, phases);
        Assert.AreEqual("DispatchStart", phases[0].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(fail ? "DispatchFault" : "DispatchComplete", phases[1].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.IsTrue(phases.All(value => value.GetProperty("Request").GetGuid() == request.Id));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("SECRET", StringComparison.Ordinal));
    }

    private sealed class ControlledHandler(Exception? failure = null) : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response = new(HttpStatusCode.OK);
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return failure is null ? Task.FromResult(_response) : Task.FromException<HttpResponseMessage>(failure);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ControlledDispatcher(Exception? failure) : IApplicationRequestDispatcher
    {
        public Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(new ApplicationResponse(request.Id, "text/plain", [],
                new Dictionary<string, string>(StringComparer.Ordinal))) : Task.FromException<ApplicationResponse>(failure);
    }
}
