using System.Data.Common;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task DomainIoPausesIdentifyQueryVersusReceiptStorageAndJoinOwnedWork(bool blob, bool assertionFailure)
    {
        var barrier = new DomainIoBarrier();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true,
            folderLockInterceptor: blob ? null : new DomainQueryBarrier(barrier),
            decorateDomainStore: blob ? inner => new DomainReceiptBarrier(inner, barrier) : null,
            decorateDispatcher: inner => new DomainDispatchCompletion(inner, barrier)).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
        var original = assertionFailure ? new AssertFailedException("Controlled I/O observation failure.") : null;
        if (original is null) await RunDomainIoControlAsync(fixture, barrier, blob).ConfigureAwait(false);
        else Assert.AreSame(original, await Assert.ThrowsAsync<AssertFailedException>(() =>
            RunDomainIoControlAsync(fixture, barrier, blob, original)).ConfigureAwait(false));
        Assert.IsTrue(barrier.Finished.Task.IsCompletedSuccessfully);
        Assert.IsTrue(barrier.SendingCompleted);
        // Fixture, request and Worker dependencies are joined before this enclosing lifetime ends.
    }

    private static async Task RunDomainIoControlAsync(CaptureFixture fixture, DomainIoBarrier barrier, bool blob,
        AssertFailedException? injectedFailure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CanonicalPath)
        {
            Content = XmlContent(Request("GetFolder", $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>")),
        };
        request.Headers.Add("X-Secret-Test-Header", "SECRET input must not appear");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Owned through direct normal await and unconditional barrier release/cancel/join below.
#pragma warning disable CA2025
        var sending = fixture.Client.SendAsync(request, cancellation.Token);
#pragma warning restore CA2025
        Exception? originalFailure = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (injectedFailure is not null) throw injectedFailure;
            await cancellation.CancelAsync().ConfigureAwait(false);
            var cancelled = false;
            try
            {
                using var response = await sending.ConfigureAwait(false);
                Assert.Fail("The controlled blocked request must cancel.");
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.IsTrue(cancelled);
            AssertDomainIoPoint(fixture, blob);
        }
        // Preserve the original assertion if owned cleanup also fails.
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
                // Dispose the successfully completed owned response after its task is joined.
#pragma warning disable VSTHRD003
                using var response = await sending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
        }
    }

    private static void AssertDomainIoPoint(CaptureFixture fixture, bool blob)
    {
        Assert.IsNotNull(fixture.Diagnostics.LastReport);
        using var report = JsonDocument.Parse(fixture.Diagnostics.LastReport);
        var events = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var started = events.Last(value => value.GetProperty("Phase").GetString() is "IoStart");
        Assert.AreEqual(blob ? "BlobPut" : "DbReader", started.GetProperty("Activity").GetString(), StringComparer.Ordinal);
        Assert.AreEqual("Mail", started.GetProperty("Operation").GetString(), StringComparer.Ordinal);
        Assert.AreEqual(events.Last(value => value.GetProperty("Phase").GetString() is "DispatchStart").GetProperty("Request").GetGuid(),
            started.GetProperty("Request").GetGuid());
        var span = started.GetProperty("Span").GetGuid();
        Assert.IsFalse(events.Any(value => value.GetProperty("Span").ValueKind is JsonValueKind.String
            && value.GetProperty("Span").GetGuid() == span && value.GetProperty("Phase").GetString() is "IoReturned"));
        Assert.AreEqual(1, report.RootElement.GetProperty("database").GetProperty("queue").GetProperty("processing").GetInt32());
        Assert.AreEqual(0, report.RootElement.GetProperty("database").GetProperty("journal").GetProperty("presentationOutbound").GetInt32());
        Assert.IsFalse(report.RootElement.GetRawText().Contains("SECRET", StringComparison.Ordinal));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("application_operation_receipts", StringComparison.Ordinal));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("owner@example.test", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DomainIoSuccessfulFolderReadRecordsRealSqlAndReceiptBlobWithoutFailureOutput()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(Request("GetFolder", $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertCode(XDocument.Parse(body), "NoError");
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        Assert.IsNull(fixture.Diagnostics.LastReport);
        using var database = JsonDocument.Parse("{}");
        using var report = JsonDocument.Parse(fixture.Diagnostics.Report(database.RootElement, "Running"));
        var events = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.IsTrue(events.Any(value => value.GetProperty("Activity").GetString() is "DbReader" && value.GetProperty("Phase").GetString() is "IoReturned"));
        Assert.IsTrue(events.Any(value => value.GetProperty("Activity").GetString() is "DbNonQuery" && value.GetProperty("Phase").GetString() is "IoReturned"));
        Assert.IsTrue(events.Any(value => value.GetProperty("Activity").GetString() is "BlobPut" && value.GetProperty("Phase").GetString() is "IoReturned"));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("owner@example.test", StringComparison.Ordinal));
    }

    private sealed class DomainIoBarrier
    {
        private int _armed = 1;
        public bool SendingCompleted { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PauseAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private sealed class DomainQueryBarrier(DomainIoBarrier barrier) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("application_operation_receipts", StringComparison.Ordinal))
                await barrier.PauseAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    private sealed class DomainReceiptBarrier(ILargeObjectStore inner, DomainIoBarrier barrier) : ILargeObjectStore
    {
        public string Provider => inner.Provider;
        public async Task<LargeObjectWriteResult> PutIfAbsentAsync(string objectName, Stream content, long length,
            string sha256, string contentType, CancellationToken cancellationToken = default)
        {
            if (string.Equals(contentType, ApplicationOperationReceiptStore.ContentType, StringComparison.Ordinal))
                await barrier.PauseAsync(cancellationToken).ConfigureAwait(false);
            return await inner.PutIfAbsentAsync(objectName, content, length, sha256, contentType, cancellationToken).ConfigureAwait(false);
        }
        public Task CopyToAsync(LargeObjectReference reference, Stream destination, CancellationToken cancellationToken = default) =>
            inner.CopyToAsync(reference, destination, cancellationToken);
        public Task<bool> DeleteIfMatchAsync(LargeObjectReference reference, CancellationToken cancellationToken = default) =>
            inner.DeleteIfMatchAsync(reference, cancellationToken);
    }

    private sealed class DomainDispatchCompletion(IApplicationRequestDispatcher inner, DomainIoBarrier barrier) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            try { return await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false); }
            finally
            {
                if (barrier.Entered.Task.IsCompleted && request.Operation is ApplicationOperations.MailOperationExecute)
                    barrier.Finished.TrySetResult();
            }
        }
    }
}
