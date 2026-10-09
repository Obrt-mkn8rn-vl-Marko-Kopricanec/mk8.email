using Azure.Core;
using Azure.Core.Pipeline;
using System.Text.Json;
using Activity = mk8.email.Messaging.Tests.GatewayFixtureDiagnostics.Activity;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task AzureSdkPausesDistinguishContainerVersusUploadAndPreserveOwnedCleanup(bool upload, bool assertionFailure)
    {
        var barrier = new DomainIoBarrier();
        var target = upload ? Activity.AzureUpload : Activity.AzureContainerCreate;
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true,
            configureDomainBlob: options => options.AddPolicy(new AzureAttemptBarrier(target, barrier), HttpPipelinePosition.BeforeTransport),
            decorateDispatcher: inner => new DomainDispatchCompletion(inner, barrier)).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
        var original = assertionFailure ? new AssertFailedException("Controlled Azure attempt observation failure.") : null;
        if (original is not null)
            Assert.AreSame(original, await Assert.ThrowsAsync<AssertFailedException>(() =>
                RunDomainIoControlAsync(fixture, barrier, blob: true, original)).ConfigureAwait(false));
        else
        {
            await RunDomainIoControlAsync(fixture, barrier, blob: true).ConfigureAwait(false);
            Assert.IsNotNull(fixture.Diagnostics.LastReport);
            using var report = JsonDocument.Parse(fixture.Diagnostics.LastReport);
            var events = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
            var started = events.Last(value => value.GetProperty("Phase").GetString() is "AzureStart");
            Assert.AreEqual(target.ToString(), started.GetProperty("Activity").GetString(), StringComparer.Ordinal);
            Assert.AreEqual("Mail", started.GetProperty("Operation").GetString(), StringComparer.Ordinal);
            Assert.AreEqual(1, started.GetProperty("Attempt").GetInt32());
            var span = started.GetProperty("Span").GetGuid();
            Assert.IsFalse(events.Any(value => value.GetProperty("Span").ValueKind is JsonValueKind.String
                && value.GetProperty("Span").GetGuid() == span && value.GetProperty("Phase").GetString() is "AzureReturned"));
        }
        Assert.IsTrue(barrier.Finished.Task.IsCompletedSuccessfully);
        Assert.IsTrue(barrier.SendingCompleted);
    }

    private sealed class AzureAttemptBarrier(Activity target, DomainIoBarrier barrier) : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => ProcessNext(message, pipeline);
        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (GatewayFixtureAzureDiagnostics.Classify(message.Request) == target)
                await barrier.PauseAsync(message.CancellationToken).ConfigureAwait(false);
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        }
    }
}
