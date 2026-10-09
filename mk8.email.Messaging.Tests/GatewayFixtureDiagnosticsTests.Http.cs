using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Activity = mk8.email.Messaging.Tests.GatewayFixtureDiagnostics.Activity;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayFixtureDiagnosticsTests
{
    private static readonly string[] HttpPhases = ["HttpRequestStart", "HttpRequestLeftQueue", "HttpRequestHeadersStart", "HttpRequestHeadersStop",
        "HttpResponseHeadersStart", "HttpResponseHeadersStop", "HttpRequestStop", "HttpRequestFailed"];
    private static readonly int[] NestedAttempts = [1, 2, 1];
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HttpLifecycleHandlesSourceCreationOrderWithoutRetainingPayload(bool sourceFirst)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var early = sourceFirst ? new EventSource(GatewayFixtureHttpDiagnostics.ControlledSourceName) : null;
        using var listener = new GatewayFixtureHttpDiagnostics(diagnostics, controlledSource: true);
        using var late = sourceFirst ? null : new EventSource(GatewayFixtureHttpDiagnostics.ControlledSourceName);
        var source = early ?? late!;
        var span = Guid.CreateVersion7();
        var request = DiagnosticRequest();
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher((_, _) =>
        {
            EmitHttp(source, "RequestStart"); // Dispatch alone is insufficient.
            using (diagnostics.EnterAzureAttempt(Activity.AzureUpload, span, 2))
            {
                EmitHttp(source, "RequestStart");
                EmitHttp(source, "RequestLeftQueue");
                EmitHttp(source, "RequestHeadersStart");
                EmitHttp(source, "RequestHeadersStop");
                EmitHttp(source, "ResponseHeadersStart");
                EmitHttp(source, "ResponseHeadersStop");
                EmitHttp(source, "RequestStop");
                EmitHttp(source, "RequestFailed");
                EmitHttp(source, "RequestFailedDetailed");
                EmitHttp(source, "ConnectionEstablished");
                EmitHttp(source, "Redirect");
                EmitHttp(source, "Unregistered_SECRET");
            }
            EmitHttp(source, "RequestStart"); // Attempt scope was restored.
            return Task.CompletedTask;
        }), diagnostics);
        await dispatcher.DispatchAsync(request).ConfigureAwait(false);
        using (diagnostics.EnterAzureAttempt(Activity.AzureUpload, span, 2)) EmitHttp(source, "RequestStart");
        var events = DiagnosticEvents(diagnostics).Where(IsHttpEvent).ToArray();
        CollectionAssert.AreEqual(HttpPhases,
            events.Select(value => value.GetProperty("Phase").GetString()).ToArray());
        Assert.IsTrue(events.All(value => value.GetProperty("Request").GetGuid() == request.Id
            && value.GetProperty("Span").GetGuid() == span && value.GetProperty("Attempt").GetInt32() == 2
            && value.GetProperty("Activity").GetString() is "AzureUpload"));
    }

    [TestMethod]
    public async Task HttpLifecycleRestoresNestedAttemptAndStopsAfterListenerDisposal()
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var source = new EventSource(GatewayFixtureHttpDiagnostics.ControlledSourceName);
        using var listener = new GatewayFixtureHttpDiagnostics(diagnostics, controlledSource: true);
        var outer = Guid.CreateVersion7();
        var nested = Guid.CreateVersion7();
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher((_, _) =>
        {
            using var scope = diagnostics.EnterAzureAttempt(Activity.AzureUpload, outer, 1);
            EmitHttp(source, "RequestStart");
            using (diagnostics.EnterAzureAttempt(Activity.AzureProperties, nested, 2)) EmitHttp(source, "RequestStart");
            EmitHttp(source, "RequestStop");
            listener.Dispose();
            EmitHttp(source, "RequestStart");
            return Task.CompletedTask;
        }), diagnostics);
        await dispatcher.DispatchAsync(DiagnosticRequest()).ConfigureAwait(false);
        var events = DiagnosticEvents(diagnostics).Where(IsHttpEvent).ToArray();
        CollectionAssert.AreEqual(new[] { outer, nested, outer }, events.Select(value => value.GetProperty("Span").GetGuid()).ToArray());
        CollectionAssert.AreEqual(NestedAttempts, events.Select(value => value.GetProperty("Attempt").GetInt32()).ToArray());
    }

    [TestMethod]
    public Task HttpLifecycleOverlappingAttemptsKeepTheirOwnImmutableCorrelation()
        => ObserveOverlappingHttpScopesAsync(cancelBeforeResume: false);

    [TestMethod]
    public Task HttpLifecycleCleanupBeforeResumeObservesCancellationWithoutInventingAnEvent()
        => ObserveOverlappingHttpScopesAsync(cancelBeforeResume: true);

    private static async Task ObserveOverlappingHttpScopesAsync(bool cancelBeforeResume)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var source = new EventSource(GatewayFixtureHttpDiagnostics.ControlledSourceName);
        using var listener = new GatewayFixtureHttpDiagnostics(diagnostics, controlledSource: true);
        var first = DiagnosticRequest();
        var second = DiagnosticRequest();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (request, token) =>
        {
            using var scope = diagnostics.EnterAzureAttempt(Activity.AzureUpload, request.Id, request.Id == first.Id ? 1 : 2);
            if (request.Id == first.Id)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            EmitHttp(source, "RequestStart");
        }), diagnostics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = dispatcher.DispatchAsync(first, deadline.Token);
        Exception? original = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await dispatcher.DispatchAsync(second, deadline.Token).ConfigureAwait(false);
            if (cancelBeforeResume)
                await GatewayEwsRouteTests.ObserveWriterCleanupAsync(pending, Task.CompletedTask, deadline, null).ConfigureAwait(false);
            else
            {
                release.TrySetResult();
                // Success requires the first emission before cleanup is permitted to cancel it.
                await pending.ConfigureAwait(false);
            }
        }
        // Original observation failures remain in the inherited owned-task cleanup inventory.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; throw; }
        finally
        {
            release.TrySetResult();
            await GatewayEwsRouteTests.ObserveWriterCleanupAsync(pending, Task.CompletedTask, deadline, original).ConfigureAwait(false);
        }
        var events = DiagnosticEvents(diagnostics).Where(IsHttpEvent).ToArray();
        Assert.HasCount(cancelBeforeResume ? 1 : 2, events);
        Assert.AreEqual(cancelBeforeResume, pending.IsCanceled);
        Assert.AreEqual(second.Id, events[0].GetProperty("Request").GetGuid());
        if (!cancelBeforeResume) Assert.AreEqual(first.Id, events[1].GetProperty("Request").GetGuid());
        Assert.IsTrue(events.All(value => value.GetProperty("Request").GetGuid() == value.GetProperty("Span").GetGuid()
            && value.GetProperty("Attempt").GetInt32() == (value.GetProperty("Request").GetGuid() == first.Id ? 1 : 2)));
    }

    [TestMethod]
    [DataRow(false, "release")]
    [DataRow(false, "assertion")]
    [DataRow(false, "cancel")]
    [DataRow(true, "release")]
    [DataRow(true, "assertion")]
    [DataRow(true, "cancel")]
    public async Task ActualHttpHeaderHoldIsObservedAndBothOwnedTasksAreJoined(bool statusLine, string disposition)
    {
        var original = disposition is "assertion" ? new AssertFailedException("Controlled header observation failure.") : null;
        if (original is null) await RunHttpHeaderControlAsync(statusLine, disposition, null).ConfigureAwait(false);
        else Assert.AreSame(original, await Assert.ThrowsAsync<AssertFailedException>(() => RunHttpHeaderControlAsync(statusLine, disposition, original)).ConfigureAwait(false));
    }

    private static async Task RunHttpHeaderControlAsync(bool statusLine, string disposition, AssertFailedException? injected)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var listener = new GatewayFixtureHttpDiagnostics(diagnostics);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var headersSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = new BlobClientOptions();
        GatewayFixtureAzureDiagnostics.Configure(options, diagnostics);
        var pipeline = HttpPipelineBuilder.Build(options); // Default SDK transport/retry/timeouts unchanged.
        using var message = pipeline.CreateMessage();
        message.Request.Method = RequestMethod.Put;
        message.Request.Uri.Reset(new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/SECRET?sig=SECRET"));
        var request = DiagnosticRequest();
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (_, token) =>
            await pipeline.SendAsync(message, token).ConfigureAwait(false)), diagnostics);
        Task serving = Task.CompletedTask;
        Task sending = Task.CompletedTask;
        Exception? failure = null;
        try
        {
            // Start owned work ONLY inside unconditional cleanup, after all dependency construction.
#pragma warning disable CA2025
            serving = ServeHeldHeadersAsync(server, headersSeen, release, statusLine, deadline.Token);
#pragma warning restore CA2025
            sending = dispatcher.DispatchAsync(request, deadline.Token);
            await AssertHttpHeadersHeldAsync(diagnostics, headersSeen, sending, request.Id, statusLine, deadline.Token).ConfigureAwait(false);
            if (injected is not null) throw injected;
            if (disposition is "cancel") await deadline.CancelAsync().ConfigureAwait(false);
            else
            {
                release.TrySetResult();
                await sending.ConfigureAwait(false);
                await serving.ConfigureAwait(false);
                Assert.AreEqual(200, message.Response.Status);
                Assert.IsTrue(DiagnosticEvents(diagnostics).Any(value => value.GetProperty("Phase").GetString() is "HttpRequestStop"));
            }
        }
        // Preserve the original failure through the existing bounded cancellation/join fault inventory.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { failure = exception; throw; }
        finally
        {
            release.TrySetResult();
            await GatewayEwsRouteTests.ObserveWriterCleanupAsync(sending, serving, deadline, failure).ConfigureAwait(false);
        }
        Assert.IsTrue(sending.IsCompleted);
        Assert.IsTrue(serving.IsCompleted);
        if (disposition is "cancel") Assert.IsTrue(sending.IsCanceled);
    }

    private static async Task AssertHttpHeadersHeldAsync(GatewayFixtureDiagnostics diagnostics, TaskCompletionSource headersSeen,
        Task sending, Guid request, bool statusLine, CancellationToken cancellationToken)
    {
        await headersSeen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var boundary = statusLine ? "HttpResponseHeadersStart" : "HttpRequestHeadersStop";
        while (!DiagnosticEvents(diagnostics).Any(value => string.Equals(value.GetProperty("Phase").GetString(), boundary, StringComparison.Ordinal)))
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        var events = DiagnosticEvents(diagnostics).Where(IsHttpEvent).ToArray();
        Assert.IsTrue(events.Any(value => value.GetProperty("Phase").GetString() is "HttpRequestHeadersStop"));
        Assert.AreEqual(statusLine, events.Any(value => value.GetProperty("Phase").GetString() is "HttpResponseHeadersStart"));
        Assert.IsFalse(events.Any(value => value.GetProperty("Phase").GetString() is "HttpResponseHeadersStop" or "HttpRequestStop"));
        Assert.IsFalse(sending.IsCompleted);
        Assert.IsTrue(events.All(value => value.GetProperty("Request").GetGuid() == request && value.GetProperty("Attempt").GetInt32() == 1));
        Assert.HasCount(1, events.Select(value => value.GetProperty("Span").GetGuid()).Distinct().ToArray());
    }

    private static async Task ServeHeldHeadersAsync(TcpListener server, TaskCompletionSource entered,
        TaskCompletionSource release, bool statusLine, CancellationToken cancellationToken)
    {
        using var socket = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var count = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
        {
            count += line.Length;
            if (count > 8192) throw new InvalidOperationException("Controlled request headers exceed budget.");
        }
        if (statusLine) await stream.WriteAsync("HTTP/1.1 200 OK\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!statusLine) await stream.WriteAsync("HTTP/1.1 200 OK\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("Content-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsHttpEvent(JsonElement value) => value.GetProperty("Phase").GetString()?.StartsWith("Http", StringComparison.Ordinal) == true;
    private static void EmitHttp(EventSource source, string name) => source.Write(name,
        new EventSourceOptions { Level = EventLevel.Informational }, new { Host = "SECRET", Uri = "SECRET", Error = "SECRET", Headers = "SECRET" });
}
