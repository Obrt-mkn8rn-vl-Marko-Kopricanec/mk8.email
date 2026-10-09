using System.Net;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Storage;
using Activity = mk8.email.Messaging.Tests.GatewayFixtureDiagnostics.Activity;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayFixtureDiagnosticsTests
{
    [TestMethod]
    public void AzureSdkObserverLeavesExistingRetryTransportAndDiagnosticDefaultsUnchanged()
    {
        var baseline = new BlobClientOptions();
        var options = new BlobClientOptions();
        GatewayFixtureAzureDiagnostics.Configure(options, new GatewayFixtureDiagnostics());
        Assert.AreSame(baseline.Transport, options.Transport);
        Assert.AreSame(baseline.RetryPolicy, options.RetryPolicy);
        Assert.AreEqual(baseline.Retry.Mode, options.Retry.Mode);
        Assert.AreEqual(baseline.Retry.MaxRetries, options.Retry.MaxRetries);
        Assert.AreEqual(baseline.Retry.Delay, options.Retry.Delay);
        Assert.AreEqual(baseline.Retry.MaxDelay, options.Retry.MaxDelay);
        Assert.AreEqual(baseline.Retry.NetworkTimeout, options.Retry.NetworkTimeout);
        Assert.AreEqual(baseline.Diagnostics.IsLoggingEnabled, options.Diagnostics.IsLoggingEnabled);
        Assert.AreEqual(baseline.Diagnostics.IsLoggingContentEnabled, options.Diagnostics.IsLoggingContentEnabled);
        Assert.AreEqual(baseline.Diagnostics.IsDistributedTracingEnabled, options.Diagnostics.IsDistributedTracingEnabled);
        Assert.AreEqual(baseline.Diagnostics.IsTelemetryEnabled, options.Diagnostics.IsTelemetryEnabled);
    }

    [TestMethod]
    [DataRow("PUT", "?restype=container&sig=SECRET", Activity.AzureContainerCreate)]
    [DataRow("PUT", "?sig=SECRET", Activity.AzureUpload)]
    [DataRow("PUT", "?comp=block&blockid=SECRET", Activity.AzureBlock)]
    [DataRow("PUT", "?comp=blocklist&sig=SECRET", Activity.AzureBlockList)]
    [DataRow("HEAD", "?sig=SECRET", Activity.AzureProperties)]
    [DataRow("GET", "?sig=SECRET", Activity.AzureDownload)]
    [DataRow("DELETE", "?sig=SECRET", Activity.AzureDelete)]
    [DataRow("PUT", "?comp=metadata&sig=SECRET", Activity.AzureOther)]
    [DataRow("GET", "?restype=container&comp=list&sig=SECRET", Activity.AzureOther)]
    [DataRow("POST", "?sig=SECRET", Activity.AzureOther)]
    [DataRow("PUT", "?comp=block&comp=blocklist&sig=SECRET", Activity.AzureOther)]
    [DataRow("PUT", "?restype=container&restype=container&sig=SECRET", Activity.AzureOther)]
    public void AzureSdkClassificationRetainsOnlyClosedStageLabels(string method, string query, Activity expected)
    {
        using var request = HttpClientTransport.Shared.CreateRequest();
        request.Uri.Reset(new Uri("https://SECRET.example.invalid/SECRET" + query));
        request.Method = new RequestMethod(method);
        Assert.AreEqual(expected, GatewayFixtureAzureDiagnostics.Classify(request));
    }

    [TestMethod]
    [DataRow(false, "success")]
    [DataRow(true, "success")]
    [DataRow(false, "fault")]
    [DataRow(true, "fault")]
    [DataRow(false, "cancel")]
    [DataRow(true, "cancel")]
    public async Task AzureSdkObserverForwardsSameMessageResultAndFailure(bool synchronous, string outcome)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var cancellation = new CancellationTokenSource();
        if (outcome is "cancel") await cancellation.CancelAsync().ConfigureAwait(false);
        Exception? original = outcome switch
        {
            "cancel" => new OperationCanceledException("SECRET cancellation", cancellation.Token),
            "fault" => new InvalidOperationException("SECRET provider failure"),
            _ => null,
        };
        using var response = new AzureUnitResponse(201);
        var terminal = new AzureUnitTerminal(response, original, cancellation.Token);
        var pipeline = new HttpPipeline(HttpClientTransport.Shared,
            [new GatewayFixtureAzureDiagnostics(diagnostics), terminal], new ResponseClassifier());
        using var message = pipeline.CreateMessage();
        message.Request.Method = RequestMethod.Put;
        message.Request.Uri.Reset(new Uri("https://SECRET.example.invalid/SECRET?sig=SECRET"));
        message.Request.Headers.SetValue("X-Secret", "SECRET header");
        var request = DiagnosticRequest();
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (_, token) =>
        {
            if (synchronous)
            {
                InvokeAzureSynchronousOverride(pipeline, message, token);
            }
            else await pipeline.SendAsync(message, token).ConfigureAwait(false);
        }), diagnostics);
        if (original is null) await dispatcher.DispatchAsync(request, cancellation.Token).ConfigureAwait(false);
        else Assert.AreSame(original, await Assert.ThrowsAsync<Exception>(() => dispatcher.DispatchAsync(request, cancellation.Token)).ConfigureAwait(false));
        Assert.AreSame(message, terminal.Seen);
        Assert.AreEqual(1, terminal.Calls);
        Assert.IsNull(message.NetworkTimeout);
        Assert.IsTrue(message.Request.Headers.TryGetValue("X-Secret", out var header));
        Assert.AreEqual("SECRET header", header, StringComparer.Ordinal);
        if (original is null) { Assert.AreSame(response, message.Response); Assert.AreEqual(0, response.Disposals); }
        var events = DiagnosticEvents(diagnostics);
        Assert.HasCount(4, events);
        Assert.AreEqual("AzureStart", events[1].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(outcome switch { "success" => "AzureReturned", "cancel" => "AzureCancelled", _ => "AzureFault" },
            events[2].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual("AzureUpload", events[1].GetProperty("Activity").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(events[1].GetProperty("Span").GetGuid(), events[2].GetProperty("Span").GetGuid());
        Assert.AreEqual(1, events[1].GetProperty("Attempt").GetInt32());
        Assert.AreEqual(1, events[2].GetProperty("Attempt").GetInt32());
        Assert.IsTrue(events.All(value => value.GetProperty("Request").GetGuid() == request.Id));
        if (original is null) Assert.AreEqual(201, events[2].GetProperty("Status").GetInt32());
        else Assert.AreEqual(System.Text.Json.JsonValueKind.Null, events[2].GetProperty("Status").ValueKind);
    }

    [TestMethod]
    public async Task AzureSdkActualRetryKeepsOneMessageSpanAndSeparatesAttemptStatuses()
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var handler = new AzureRetryHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        using var transport = new HttpClientTransport(client);
        var options = new BlobClientOptions { Transport = transport };
        options.Retry.Mode = RetryMode.Fixed;
        options.Retry.Delay = TimeSpan.FromMilliseconds(1);
        options.Retry.MaxRetries = 1; // Controlled unit only, never the maintained fixture/client defaults.
        GatewayFixtureAzureDiagnostics.Configure(options, diagnostics);
        var pipeline = HttpPipelineBuilder.Build(options);
        using var message = pipeline.CreateMessage();
        message.Request.Method = RequestMethod.Put;
        message.Request.Uri.Reset(new Uri("https://SECRET.example.invalid/SECRET?sig=SECRET"));
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (_, token) =>
            await pipeline.SendAsync(message, token).ConfigureAwait(false)), diagnostics);
        await dispatcher.DispatchAsync(DiagnosticRequest()).ConfigureAwait(false);
        Assert.AreEqual(2, handler.Calls);
        var events = DiagnosticEvents(diagnostics).Where(value => value.GetProperty("Phase").GetString() is "AzureReturned").ToArray();
        Assert.HasCount(2, events);
        Assert.AreEqual(events[0].GetProperty("Span").GetGuid(), events[1].GetProperty("Span").GetGuid());
        Assert.AreEqual(1, events[0].GetProperty("Attempt").GetInt32());
        Assert.AreEqual(2, events[1].GetProperty("Attempt").GetInt32());
        Assert.AreEqual(503, events[0].GetProperty("Status").GetInt32());
        Assert.AreEqual(201, events[1].GetProperty("Status").GetInt32());
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    [TestCategory("AzureBlobCompatible")]
    public async Task AzureSdkActualStoreRoundTripDistinguishesConflictLookupReadAndDeletion()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var scope = fixture.DomainScopes.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var store = scope.ServiceProvider.GetRequiredService<ILargeObjectStore>();
        var content = "SECRET stored bytes"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (_, token) =>
        {
            using var first = new MemoryStream(content, writable: false);
            var written = await store.PutIfAbsentAsync("SECRET-object", first, content.Length, hash, "application/octet-stream", token).ConfigureAwait(false);
            Assert.IsTrue(written.Created);
            using var repeated = new MemoryStream(content, writable: false);
            var existing = await store.PutIfAbsentAsync("SECRET-object", repeated, content.Length, hash, "application/octet-stream", token).ConfigureAwait(false);
            Assert.IsFalse(existing.Created);
            Assert.AreEqual(written.Reference, existing.Reference);
            using var destination = new MemoryStream();
            await store.CopyToAsync(written.Reference, destination, token).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, destination.ToArray());
            Assert.IsTrue(await store.DeleteIfMatchAsync(written.Reference, token).ConfigureAwait(false));
            Assert.IsFalse(await store.DeleteIfMatchAsync(written.Reference, token).ConfigureAwait(false));
        }), fixture.Diagnostics);
        await dispatcher.DispatchAsync(DiagnosticRequest()).ConfigureAwait(false);
        var events = DiagnosticEvents(fixture.Diagnostics).Where(value => value.GetProperty("Phase").GetString() is "AzureReturned").ToArray();
        foreach (var stage in new[] { "AzureContainerCreate", "AzureUpload", "AzureProperties", "AzureDownload", "AzureDelete" })
            Assert.IsTrue(events.Any(value => string.Equals(value.GetProperty("Activity").GetString(), stage, StringComparison.Ordinal)), stage);
        Assert.IsTrue(events.Any(value => value.GetProperty("Activity").GetString() is "AzureUpload" && value.GetProperty("Status").GetInt32() is 409 or 412));
        Console.WriteLine("MK8_TEST_AZURE_STAGE_CONTROL " + System.Text.Json.JsonSerializer.Serialize(events.Select(value => new
        {
            Activity = value.GetProperty("Activity").GetString(),
            Status = value.GetProperty("Status").GetInt32(),
        })));
        Assert.IsNull(fixture.Diagnostics.LastReport);
    }

    // This synchronous controlled pipeline ends in AzureUnitTerminal, never a network/blocking transport.
    private static void InvokeAzureSynchronousOverride(HttpPipeline pipeline, HttpMessage message, CancellationToken token) =>
        pipeline.Send(message, token);

    private sealed class AzureUnitTerminal(Response response, Exception? failure, CancellationToken token) : HttpPipelinePolicy
    {
        public HttpMessage? Seen { get; private set; }
        public int Calls { get; private set; }
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Seen = message;
            Calls++;
            Assert.AreEqual(token, message.CancellationToken);
            if (failure is not null) throw failure;
            message.Response = response;
        }
        public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        { Process(message, pipeline); return ValueTask.CompletedTask; }
    }

    private sealed class AzureUnitResponse(int status) : Response
    {
        public int Disposals { get; private set; }
        public override int Status => status;
        public override string ReasonPhrase => "SECRET reason";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "SECRET request";
        public override void Dispose() { Disposals++; ContentStream?.Dispose(); }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = string.Empty; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }

    private sealed class AzureRetryHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(++Calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created)
            { Content = new StringContent("SECRET response"), RequestMessage = request });
    }
}
