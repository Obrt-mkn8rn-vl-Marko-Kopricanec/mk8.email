using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayFixtureDiagnosticsTests
{
    [TestMethod]
    [DataRow("send", "success")]
    [DataRow("send", "fault")]
    [DataRow("send", "cancel")]
    [DataRow("complete", "success")]
    [DataRow("complete", "fault")]
    [DataRow("complete", "cancel")]
    [DataRow("renew", "success")]
    [DataRow("renew", "fault")]
    [DataRow("renew", "cancel")]
    [DataRow("fail", "success")]
    [DataRow("fail", "fault")]
    [DataRow("fail", "cancel")]
    public async Task TransportObserverForwardsOriginalArgumentsResultAndException(string method, string outcome)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var cancellation = new CancellationTokenSource();
        if (outcome is "cancel") await cancellation.CancelAsync().ConfigureAwait(false);
        Exception? original = outcome switch
        {
            "cancel" => new OperationCanceledException("SECRET cancellation", cancellation.Token),
            "fault" => new InvalidOperationException("SECRET transport failure"),
            _ => null,
        };
        var request = DiagnosticRequest();
        var response = new ApplicationResponse(request.Id, "text/plain", "SECRET content"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal));
        var stub = new DiagnosticBusStub(request, response, cancellation.Token, original);
        var client = new GatewayFixtureTransportDiagnostics.Client(stub, diagnostics);
        var consumer = new GatewayFixtureTransportDiagnostics.Consumer(stub, diagnostics);
        Func<Task> invoke = method switch
        {
            "send" => () => AssertSendResultAsync(client, request, response, cancellation.Token),
            "complete" => () => consumer.CompleteAsync(stub.Lease, response, cancellation.Token),
            "renew" => () => AssertRenewalResultAsync(consumer, stub.Lease, true, cancellation.Token),
            _ => () => consumer.FailAsync(stub.Lease, "SECRET code", "SECRET detail", cancellation.Token),
        };
        if (original is null) await invoke().ConfigureAwait(false);
        else Assert.AreSame(original, await Assert.ThrowsAsync<Exception>(invoke).ConfigureAwait(false));
        Assert.AreEqual(method, stub.SeenMethod, StringComparer.Ordinal);
        Assert.AreEqual(1, stub.Calls);
        AssertTransportPair(diagnostics, request.Id, method, outcome);
    }

    private static async Task AssertSendResultAsync(GatewayFixtureTransportDiagnostics.Client client, ApplicationRequest request,
        ApplicationResponse expected, CancellationToken token) =>
        Assert.AreSame(expected, await client.SendAsync(request, token).ConfigureAwait(false));

    private static async Task AssertRenewalResultAsync(GatewayFixtureTransportDiagnostics.Consumer consumer, ApplicationRequestLease lease,
        bool expected, CancellationToken token) =>
        Assert.AreEqual(expected, await consumer.RenewLeaseAsync(lease, token).ConfigureAwait(false));

    private static void AssertTransportPair(GatewayFixtureDiagnostics diagnostics, Guid request, string method, string outcome)
    {
        var events = DiagnosticEvents(diagnostics);
        Assert.HasCount(2, events);
        Assert.AreEqual("TransportStart", events[0].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(outcome switch { "success" => "TransportReturned", "cancel" => "TransportCancelled", _ => "TransportFault" },
            events[1].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(method switch { "send" => "ClientBusSend", "complete" => "ResponseComplete", "renew" => "LeaseRenew", _ => "RequestFail" },
            events[0].GetProperty("Activity").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(events[0].GetProperty("Span").GetGuid(), events[1].GetProperty("Span").GetGuid());
        Assert.IsTrue(events.All(value => value.GetProperty("Request").GetGuid() == request
            && value.GetProperty("Operation").GetString() is "Mail"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RenewalReturnedMeansCallReturnNotProofOfOwnership(bool renewed)
    {
        var request = DiagnosticRequest();
        var stub = new DiagnosticBusStub(request, new ApplicationResponse(request.Id, "text/plain", [],
            new Dictionary<string, string>(StringComparer.Ordinal)), CancellationToken.None)
        { Renewed = renewed };
        var diagnostics = new GatewayFixtureDiagnostics();
        var consumer = new GatewayFixtureTransportDiagnostics.Consumer(stub, diagnostics);
        await AssertRenewalResultAsync(consumer, stub.Lease, renewed, CancellationToken.None).ConfigureAwait(false);
        AssertTransportPair(diagnostics, request.Id, "renew", "success");
    }

    [TestMethod]
    [DataRow("enqueue")]
    [DataRow("wait-response")]
    [DataRow("get")]
    [DataRow("wait-request")]
    [DataRow("claim")]
    public void UnobservedScansAndDirectMethodsReturnTheOriginalTask(string method)
    {
        var request = DiagnosticRequest();
        using var cancellation = new CancellationTokenSource();
        var stub = new DiagnosticBusStub(request, new ApplicationResponse(request.Id, "text/plain", [],
            new Dictionary<string, string>(StringComparer.Ordinal)), cancellation.Token);
        var diagnostics = new GatewayFixtureDiagnostics();
        var consumer = new GatewayFixtureTransportDiagnostics.Consumer(stub, diagnostics);
        var client = new GatewayFixtureTransportDiagnostics.Client(stub, diagnostics);
        Task task = method switch
        {
            "enqueue" => client.EnqueueAsync(request, cancellation.Token),
            "wait-response" => client.WaitForResponseAsync(request.Id, request.Deadline, cancellation.Token),
            "get" => client.GetAsync(request.Id, cancellation.Token),
            "wait-request" => consumer.WaitForRequestAsync("SECRET worker", cancellation.Token),
            _ => consumer.TryClaimAsync("SECRET worker", cancellation.Token),
        };
        Assert.AreSame(stub.ReturnedTask, task);
        Assert.IsTrue(task.IsCompletedSuccessfully);
        Assert.AreEqual(method, stub.SeenMethod, StringComparer.Ordinal);
        Assert.HasCount(0, DiagnosticEvents(diagnostics));
    }

    private sealed class DiagnosticBusStub(ApplicationRequest request, ApplicationResponse response,
        CancellationToken token, Exception? failure = null) : IApplicationRequestClient, IApplicationRequestConsumer
    {
        public ApplicationRequestLease Lease { get; } = new(request, "SECRET worker", DateTimeOffset.UtcNow.AddMinutes(1), 1);
        public bool Renewed { get; init; } = true;
        public int Calls { get; private set; }
        public string? SeenMethod { get; private set; }
        public Task? ReturnedTask { get; private set; }

        private Task<T> ReturnAsync<T>(string method, T result, CancellationToken observed)
        {
            Assert.AreEqual(token, observed);
            Calls++;
            SeenMethod = method;
            var task = failure is null ? Task.FromResult(result) : Task.FromException<T>(failure);
            ReturnedTask = task;
            return task;
        }

        private void AssertLease(ApplicationRequestLease lease) => Assert.AreSame(Lease, lease);
        public Task<ApplicationResponse> SendAsync(ApplicationRequest value, CancellationToken cancellationToken = default)
        { Assert.AreSame(request, value); return ReturnAsync("send", response, cancellationToken); }
        public Task EnqueueAsync(ApplicationRequest value, CancellationToken cancellationToken = default)
        { Assert.AreSame(request, value); return ReturnAsync("enqueue", true, cancellationToken); }
        public Task<ApplicationResponse> WaitForResponseAsync(Guid requestId, DateTimeOffset deadline, CancellationToken cancellationToken = default)
        { Assert.AreEqual(request.Id, requestId); Assert.AreEqual(request.Deadline, deadline); return ReturnAsync("wait-response", response, cancellationToken); }
        public Task<ApplicationExchangeSnapshot?> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
        { Assert.AreEqual(request.Id, requestId); return ReturnAsync<ApplicationExchangeSnapshot?>("get", null, cancellationToken); }
        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default)
        { Assert.AreEqual(Lease.WorkerId, workerId, StringComparer.Ordinal); return ReturnAsync("wait-request", Lease, cancellationToken); }
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default)
        { Assert.AreEqual(Lease.WorkerId, workerId, StringComparer.Ordinal); return ReturnAsync<ApplicationRequestLease?>("claim", Lease, cancellationToken); }
        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default)
        { AssertLease(lease); return ReturnAsync("renew", Renewed, cancellationToken); }
        public Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse value, CancellationToken cancellationToken = default)
        { AssertLease(lease); Assert.AreSame(response, value); return ReturnAsync("complete", true, cancellationToken); }
        public Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail, CancellationToken cancellationToken = default)
        {
            AssertLease(lease);
            Assert.AreEqual("SECRET code", errorCode, StringComparer.Ordinal);
            Assert.AreEqual("SECRET detail", errorDetail, StringComparer.Ordinal);
            return ReturnAsync("fail", true, cancellationToken);
        }
    }
}
