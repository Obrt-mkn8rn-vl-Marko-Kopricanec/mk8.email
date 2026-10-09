using System.Net;
using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledRealEwsRequestReportsCaptureQueueAndBlockedWorkerWithoutDisclosingInput(bool clientTimeout)
    {
        var barrier = new DiagnosticDispatchBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true,
            decorateDispatcher: inner => new DiagnosticBlockingDispatcher(inner, barrier)).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
        await RunDiagnosticCancellationControlAsync(fixture, barrier, clientTimeout).ConfigureAwait(false);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
    }

    [TestMethod]
    public async Task DiagnosticAssertionFailureCancelsAndJoinsOwnedWorkBeforeDisposal()
    {
        var barrier = new DiagnosticDispatchBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true,
            decorateDispatcher: inner => new DiagnosticBlockingDispatcher(inner, barrier)).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var original = new AssertFailedException("Controlled diagnostic observation assertion failure.");
        var error = await Assert.ThrowsAsync<AssertFailedException>(() => RunDiagnosticCancellationControlAsync(fixture, barrier,
            clientTimeout: false, original)).ConfigureAwait(false);
        Assert.AreSame(original, error);
        Assert.IsTrue(barrier.SendingCompleted);
        Assert.IsTrue(barrier.Finished.Task.IsCompletedSuccessfully);
        // Request resources were disposed only after their task joined; fixture dependencies remain alive here.
    }

    private static async Task RunDiagnosticCancellationControlAsync(CaptureFixture fixture, DiagnosticDispatchBarrier barrier,
        bool clientTimeout, AssertFailedException? injectedFailure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CanonicalPath)
        {
            Content = XmlContent(MutationRequest("DeleteFolder", "<m:FolderIds><t:DistinguishedFolderId Id='msgfolderroot'/></m:FolderIds>")),
        };
        request.Headers.Add("X-Secret-Test-Header", "SECRET HEADER MUST NOT BE REPORTED");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(clientTimeout ? 30 : 10));
        // Direct normal await plus unconditional finally cancellation/owned-task observation precede disposal.
        // The injected-assertion route proves both client work and Worker dispatch complete with dependencies alive.
#pragma warning disable CA2025
        var sending = fixture.Client.SendAsync(request, cancellation.Token);
#pragma warning restore CA2025
        Exception? originalFailure = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (injectedFailure is not null) throw injectedFailure;
            if (!clientTimeout) await cancellation.CancelAsync().ConfigureAwait(false);
            OperationCanceledException? failure = null;
            try
            {
                using var response = await sending.ConfigureAwait(false);
                Assert.Fail("The controlled blocked HTTP request must cancel.");
            }
            catch (OperationCanceledException exception) { failure = exception; }
            Assert.IsNotNull(failure);
            if (clientTimeout) Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);
            Assert.IsTrue(sending.IsCompleted);
            AssertDiagnosticSnapshot(fixture);
        }
        // Keep the initiating assertion/transport fault if the bounded owned-task cleanup also fails.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { originalFailure = exception; throw; }
        finally
        {
            barrier.Release.TrySetResult();
            await ObserveWriterCleanupAsync(sending, barrier.Entered.Task.IsCompleted ? barrier.Finished.Task : null,
                cancellation, originalFailure).ConfigureAwait(false);
            barrier.SendingCompleted = sending.IsCompleted;
            if (sending.IsCompletedSuccessfully)
            {
                // This completed owned task may have succeeded before an injected assertion failed.
#pragma warning disable VSTHRD003
                using var response = await sending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
        }
    }

    private static void AssertDiagnosticSnapshot(CaptureFixture fixture)
    {
        Assert.IsNotNull(fixture.Diagnostics.LastReport);
        using var report = JsonDocument.Parse(fixture.Diagnostics.LastReport);
        var database = report.RootElement.GetProperty("database");
        Assert.AreEqual(1, database.GetProperty("queue").GetProperty("processing").GetInt32());
        Assert.AreEqual(1, database.GetProperty("journal").GetProperty("presentationInbound").GetInt32());
        Assert.AreEqual(0, database.GetProperty("journal").GetProperty("presentationOutbound").GetInt32());
        var phases = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.IsTrue(phases.Any(value => value.GetProperty("Phase").GetString() is "GatewayEnter"));
        Assert.IsTrue(phases.Any(value => value.GetProperty("Phase").GetString() is "JournalInboundComplete"));
        Assert.IsTrue(phases.Any(value => value.GetProperty("Phase").GetString() is "DispatchStart"
            && value.GetProperty("Operation").GetString() is "Profile"));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("SECRET", StringComparison.Ordinal));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("owner@example.test", StringComparison.Ordinal));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("test-protocol-secret", StringComparison.Ordinal));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("msgfolderroot", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RealEwsRefusalRetainsOriginalDeadlineAssertionsAndProducesNoFailureReport()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
        AssertCode(await WriteAsync(fixture, "DeleteFolder", "<m:FolderIds><t:DistinguishedFolderId Id='msgfolderroot'/></m:FolderIds>").ConfigureAwait(false),
            "ErrorAccessDenied");
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
        Assert.IsNull(fixture.Diagnostics.LastReport);
    }

    private sealed class DiagnosticDispatchBarrier
    {
        public bool SendingCompleted { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DiagnosticBlockingDispatcher(IApplicationRequestDispatcher inner, DiagnosticDispatchBarrier barrier)
        : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            barrier.Entered.TrySetResult();
            try
            {
                await barrier.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally { barrier.Finished.TrySetResult(); }
        }
    }
}
