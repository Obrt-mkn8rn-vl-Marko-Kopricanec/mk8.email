using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayFixtureDiagnosticsTests
{
    [TestMethod]
    [DataRow("put", "success")]
    [DataRow("read", "success")]
    [DataRow("delete", "success")]
    [DataRow("put", "fault")]
    [DataRow("read", "fault")]
    [DataRow("delete", "fault")]
    [DataRow("put", "cancel")]
    [DataRow("read", "cancel")]
    [DataRow("delete", "cancel")]
    public async Task BlobBoundariesForwardOriginalValuesAndCorrelateWithoutSecretData(string kind, string outcome)
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        using var cancellation = new CancellationTokenSource();
        if (outcome is "cancel") await cancellation.CancelAsync().ConfigureAwait(false);
        Exception? original = outcome switch
        {
            "cancel" => new OperationCanceledException("SECRET cancellation", cancellation.Token),
            "fault" => new InvalidOperationException("SECRET provider error"),
            _ => null,
        };
        using var stream = new MemoryStream("SECRET bytes"u8.ToArray());
        var reference = new LargeObjectReference("SECRET provider", "SECRET name", stream.Length, "SECRET hash", "SECRET etag");
        var inner = new DiagnosticUnitStore(reference, stream, original, cancellation.Token);
        var store = new GatewayFixtureBlobDiagnostics(inner, diagnostics);
        Assert.AreEqual(reference.Provider, store.Provider, StringComparer.Ordinal);
        var request = DiagnosticRequest();
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (_, token) =>
        {
            if (kind is "put") Assert.AreSame(inner.Written,
                await store.PutIfAbsentAsync(reference.ObjectName, stream, reference.Length, reference.Sha256, "SECRET type", token).ConfigureAwait(false));
            else if (kind is "read") await store.CopyToAsync(reference, stream, token).ConfigureAwait(false);
            else Assert.IsFalse(await store.DeleteIfMatchAsync(reference, token).ConfigureAwait(false));
        }), diagnostics);
        if (original is null) Assert.AreEqual(request.Id, (await dispatcher.DispatchAsync(request, cancellation.Token).ConfigureAwait(false)).RequestId);
        else Assert.AreSame(original, await Assert.ThrowsAsync<Exception>(() => dispatcher.DispatchAsync(request, cancellation.Token)).ConfigureAwait(false));
        Assert.AreEqual(1, inner.Calls);
        var events = DiagnosticEvents(diagnostics);
        Assert.HasCount(4, events);
        Assert.AreEqual("IoStart", events[1].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(outcome switch { "success" => "IoReturned", "cancel" => "IoCancelled", _ => "IoFault" },
            events[2].GetProperty("Phase").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(kind switch { "put" => "BlobPut", "read" => "BlobRead", _ => "BlobDelete" },
            events[1].GetProperty("Activity").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(events[1].GetProperty("Span").GetGuid(), events[2].GetProperty("Span").GetGuid());
        Assert.IsTrue(events.All(value => value.GetProperty("Request").GetGuid() == request.Id));
    }

    [TestMethod]
    public void WorkOutsideDispatchHasNoInventedRequestCorrelation()
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        diagnostics.RecordIo(GatewayFixtureDiagnostics.Phase.IoStart, GatewayFixtureDiagnostics.Activity.BlobPut, Guid.CreateVersion7());
        Assert.HasCount(0, DiagnosticEvents(diagnostics));
    }

    [TestMethod]
    public async Task NestedDispatchRestoresOuterCorrelationAndClearsItAfterward()
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        var outer = DiagnosticRequest();
        var nested = DiagnosticRequest();
        var nestedDispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher((_, _) =>
        {
            diagnostics.RecordIo(GatewayFixtureDiagnostics.Phase.IoStart, GatewayFixtureDiagnostics.Activity.DbScalar, Guid.CreateVersion7());
            return Task.CompletedTask;
        }), diagnostics);
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (_, token) =>
        {
            await nestedDispatcher.DispatchAsync(nested, token).ConfigureAwait(false);
            diagnostics.RecordIo(GatewayFixtureDiagnostics.Phase.IoReturned, GatewayFixtureDiagnostics.Activity.DbScalar, Guid.CreateVersion7());
        }), diagnostics);
        await dispatcher.DispatchAsync(outer).ConfigureAwait(false);
        diagnostics.RecordIo(GatewayFixtureDiagnostics.Phase.IoStart, GatewayFixtureDiagnostics.Activity.BlobDelete, Guid.CreateVersion7());
        var events = DiagnosticEvents(diagnostics).Where(value => value.GetProperty("Activity").GetString() is "DbScalar").ToArray();
        Assert.HasCount(2, events);
        Assert.AreEqual(nested.Id, events[0].GetProperty("Request").GetGuid());
        Assert.AreEqual(outer.Id, events[1].GetProperty("Request").GetGuid());
        Assert.IsFalse(DiagnosticEvents(diagnostics).Any(value => value.GetProperty("Activity").GetString() is "BlobDelete"));
    }

    [TestMethod]
    public async Task OverlappingDispatchesDoNotShareMutableCorrelation()
    {
        var diagnostics = new GatewayFixtureDiagnostics();
        var first = DiagnosticRequest();
        var second = DiagnosticRequest();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new GatewayFixtureDiagnostics.Dispatcher(new DiagnosticUnitDispatcher(async (request, token) =>
        {
            if (request.Id == first.Id)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            diagnostics.RecordIo(GatewayFixtureDiagnostics.Phase.IoReturned,
                request.Id == first.Id ? GatewayFixtureDiagnostics.Activity.BlobPut : GatewayFixtureDiagnostics.Activity.DbReader,
                Guid.CreateVersion7());
        }), diagnostics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = dispatcher.DispatchAsync(first, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await dispatcher.DispatchAsync(second, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
            await pending.ConfigureAwait(false);
        }
        var events = DiagnosticEvents(diagnostics).Where(value => value.GetProperty("Phase").GetString() is "IoReturned").ToArray();
        Assert.HasCount(2, events);
        Assert.AreEqual(second.Id, events[0].GetProperty("Request").GetGuid());
        Assert.AreEqual(first.Id, events[1].GetProperty("Request").GetGuid());
    }

    private static ApplicationRequest DiagnosticRequest() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "ews",
        ApplicationOperations.MailOperationExecute, "application/json", "SECRET payload"u8.ToArray(),
        new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private static JsonElement[] DiagnosticEvents(GatewayFixtureDiagnostics diagnostics)
    {
        using var database = JsonDocument.Parse("{}");
        using var report = JsonDocument.Parse(diagnostics.Report(database.RootElement, "Running"));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("SECRET", StringComparison.Ordinal));
        return report.RootElement.GetProperty("events").EnumerateArray().Select(value => value.Clone()).ToArray();
    }

    private sealed class DiagnosticUnitDispatcher(Func<ApplicationRequest, CancellationToken, Task> invoke) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            await invoke(request, cancellationToken).ConfigureAwait(false);
            return new(request.Id, "application/json", [], new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    private sealed class DiagnosticUnitStore(LargeObjectReference reference, Stream stream, Exception? failure, CancellationToken token)
        : ILargeObjectStore
    {
        public string Provider => reference.Provider;
        public LargeObjectWriteResult Written { get; } = new(reference, true);
        public int Calls { get; private set; }
        public Task<LargeObjectWriteResult> PutIfAbsentAsync(string objectName, Stream content, long length, string sha256,
            string contentType, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(reference.ObjectName, objectName, StringComparer.Ordinal);
            Assert.AreSame(stream, content);
            Assert.AreEqual(reference.Length, length);
            Assert.AreEqual(reference.Sha256, sha256, StringComparer.Ordinal);
            Assert.AreEqual("SECRET type", contentType, StringComparer.Ordinal);
            Observe(cancellationToken);
            return failure is null ? Task.FromResult(Written) : Task.FromException<LargeObjectWriteResult>(failure);
        }
        public Task CopyToAsync(LargeObjectReference value, Stream destination, CancellationToken cancellationToken = default)
        {
            Assert.AreSame(reference, value);
            Assert.AreSame(stream, destination);
            Observe(cancellationToken);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
        public Task<bool> DeleteIfMatchAsync(LargeObjectReference value, CancellationToken cancellationToken = default)
        {
            Assert.AreSame(reference, value);
            Observe(cancellationToken);
            return failure is null ? Task.FromResult(false) : Task.FromException<bool>(failure);
        }
        private void Observe(CancellationToken cancellationToken)
        {
            Assert.AreEqual(token, cancellationToken);
            Calls++;
        }
    }
}
