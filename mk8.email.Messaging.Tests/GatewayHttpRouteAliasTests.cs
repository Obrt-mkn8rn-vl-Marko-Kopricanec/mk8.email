using System.Net;
using System.Text;
using System.Text.Json;
using mk8.email.Contracts.Messaging;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals creates this integration class; discovery is verified by the retained executed test counts.")]
internal sealed class GatewayHttpRouteAliasTests
{
    [TestMethod]
    [DataRow("/.WELL-KNOWN/OAUTH-AUTHORIZATION-SERVER/", "oauth", 200, false)]
    [DataRow("/.WELL-KNOWN/OAUTH-AUTHORIZATION-SERVER/", "oauth", 200, true)]
    [DataRow("/.Well-Known/OpenID-Configuration/", "oauth", 200, false)]
    [DataRow("/.Well-Known/OpenID-Configuration/", "oauth", 200, true)]
    [DataRow("/.Well-Known/JMAP/", "jmap", 200, false)]
    [DataRow("/.Well-Known/JMAP/", "jmap", 200, true)]
    [DataRow("/.WELL-KNOWN/CALDAV/", "dav", 301, false)]
    [DataRow("/.WELL-KNOWN/CALDAV/", "dav", 301, true)]
    [DataRow("/.Well-Known/CardDAV/", "dav", 301, false)]
    [DataRow("/.Well-Known/CardDAV/", "dav", 301, true)]
    [DataRow("/JMAP/SESSION/", "jmap", 200, false)]
    [DataRow("/JMAP/SESSION/", "jmap", 200, true)]
    public async Task DiscoveryAliasesStayInsideDurableProtocolBoundary(string path, string protocol, int status, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        using var response = await fixture.Client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        if (status == 200)
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        else
            Assert.AreEqual("/dav/", response.Headers.Location?.OriginalString, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync(protocol, path, status, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("/JMAP/api/", "jmap", "POST", 415, false)]
    [DataRow("/JMAP/api/", "jmap", "POST", 415, true)]
    [DataRow("/OAuth/TOKEN/", "oauth", "POST", 400, false)]
    [DataRow("/OAuth/TOKEN/", "oauth", "POST", 400, true)]
    [DataRow("/DAV/", "dav", "OPTIONS", 200, false)]
    [DataRow("/DAV/", "dav", "OPTIONS", 200, true)]
    public async Task NamespaceAliasesDispatchRegisteredRoutesAndRecordTheirResponses(string path, string protocol,
        string method, int status, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        await fixture.AssertRecordedResponseAsync(protocol, path, status, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("/JMAP/api/", "jmap", "application/problem+json", false)]
    [DataRow("/JMAP/api/", "jmap", "application/problem+json", true)]
    [DataRow("/OAuth/TOKEN/", "oauth", "application/json", false)]
    [DataRow("/OAuth/TOKEN/", "oauth", "application/json", true)]
    [DataRow("/.WELL-KNOWN/OAUTH-AUTHORIZATION-SERVER/", "oauth", "application/json", false)]
    [DataRow("/.WELL-KNOWN/OAUTH-AUTHORIZATION-SERVER/", "oauth", "application/json", true)]
    [DataRow("/.Well-Known/JMAP/", "jmap", "application/problem+json", false)]
    [DataRow("/.Well-Known/JMAP/", "jmap", "application/problem+json", true)]
    [DataRow("/DAV/", "dav", "application/problem+json", true)]
    public async Task UnexpectedAliasFailureHasJournaledProtocolJsonInsteadOfAdministrativeHtml(string path,
        string protocol, string mediaType, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Test-Failure", "true");
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(mediaType, response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        Assert.IsFalse(body.Contains("deliberate secret exception", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync(protocol, path, 500, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("/OAuth/TOKEN/", false)]
    [DataRow("/OAuth/TOKEN/", true)]
    [DataRow("/.Well-Known/OpenID-Configuration/", true)]
    public async Task UnavailableWorkerUsesOAuthJsonForAllOAuthAliases(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Test-Unavailable", "true");
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        StringAssert.Contains(body, "temporarily_unavailable", StringComparison.Ordinal);
        await fixture.AssertRecordedResponseAsync("oauth", path, 503, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KnownLengthAliasRejectionPrecedesRouteAndWorkerDispatch(bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var content = new ByteArrayContent(new byte[65_537]);
        using var response = await fixture.Client.PostAsync(new Uri("/OAuth/TOKEN/", UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        StringAssert.Contains(body, "invalid_request", StringComparison.Ordinal);
        await fixture.AssertRecordedResponseAsync("oauth", "/OAuth/TOKEN/", 413, body, rejection: true).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.EndpointCalls);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("/jmap/event", false)]
    [DataRow("/jmap/EVENT", false)]
    [DataRow("/jmap/event/", false)]
    [DataRow("/JMAP/EVENT/", false)]
    [DataRow("/jmap/event", true)]
    [DataRow("/jmap/EVENT", true)]
    [DataRow("/jmap/event/", true)]
    [DataRow("/JMAP/EVENT/", true)]
    public async Task EventAliasesDeliverDurableFirstEventWhileHandlerRemainsOpen(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = EventRequest(path);
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var firstEvent = await ReadEventAsync(reader, deadline.Token).ConfigureAwait(false);
        StringAssert.Contains(firstEvent, "event: state", StringComparison.Ordinal);
        await fixture.Application.PollWaiting.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        Assert.IsFalse(fixture.EventHandlerCompleted.Task.IsCompleted);
        await AssertDurableEventAsync(fixture, path, firstEvent).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("/jmap/EVENT", 1L)]
    [DataRow("/jmap/event/", 1L)]
    [DataRow("/JMAP/EVENT/", 2L)]
    [DataRow("/jmap/event/", 2L)]
    public async Task EventAliasJournalFailureWithholdsUnrecordedStartOrFirstChunk(string path, long failSequence)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, failSequence).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = EventRequest(path);
        var body = await ReadFailedStreamAsync(fixture.Client, request, failSequence, deadline.Token).ConfigureAwait(false);
        Assert.IsFalse(body.Contains("event: state", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("ADMIN ERROR HTML", StringComparison.Ordinal));
        var failed = await fixture.Faults.Failure.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(failSequence, failed.Sequence);
        var records = await fixture.ReadPresentationSessionAsync().ConfigureAwait(false);
        Assert.AreEqual(failSequence, (long)records.Count);
        Assert.IsFalse(records.Any(record => record.Sequence >= failSequence));
    }

    [TestMethod]
    [DataRow("/jmap/EVENT")]
    [DataRow("/jmap/event/")]
    public async Task EventAliasJournalFailureAbortsOpenStreamBeforeNextEvent(string path)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = EventRequest(path);
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var firstEvent = await ReadEventAsync(reader, deadline.Token).ConfigureAwait(false);
        await fixture.Application.PollWaiting.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await AssertDurableEventAsync(fixture, path, firstEvent).ConfigureAwait(false);
        fixture.Faults.FailNextChunk = true;
        fixture.Application.ReleasePoll(changes: true);
        var failed = await fixture.Faults.Failure.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadToEndAsync(deadline.Token)).ConfigureAwait(false);
        var records = await fixture.ReadPresentationSessionAsync().ConfigureAwait(false);
        Assert.IsFalse(records.Any(record => record.Sequence >= failed.Sequence));
    }

    private static HttpRequestMessage EventRequest(string path) =>
        new(HttpMethod.Get, $"{path}?types=*&closeafter=no&ping=0");

    private static async Task<string> ReadEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            result.Append(line).Append('\n');
            if (line.Length == 0)
                return result.ToString();
        }
        Assert.Fail("The event stream ended before its first complete event.");
        return string.Empty;
    }

    private static async Task AssertDurableEventAsync(CaptureFixture fixture, string path, string firstEvent)
    {
        var records = await fixture.ReadPresentationSessionAsync().ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(3, records.Count);
        Assert.IsTrue(records.All(record => string.Equals(record.Protocol, "jmap", StringComparison.Ordinal)));
        using var inbound = JsonDocument.Parse(records[0].Payload);
        Assert.AreEqual(path, inbound.RootElement.GetProperty("path").GetString(), StringComparer.Ordinal);
        using var start = JsonDocument.Parse(records[1].Payload);
        Assert.AreEqual(200, start.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual("text/event-stream", start.RootElement.GetProperty("contentType").GetString(), StringComparer.Ordinal);
        var recorded = new StringBuilder();
        foreach (var record in records.Skip(2))
        {
            Assert.AreEqual(GatewayTrafficDirections.Outbound, record.Direction, StringComparer.Ordinal);
            using var chunk = JsonDocument.Parse(record.Payload);
            recorded.Append(Encoding.UTF8.GetString(Convert.FromBase64String(chunk.RootElement.GetProperty("bodyBase64").GetString()!)));
        }
        Assert.AreEqual(firstEvent, recorded.ToString(), StringComparer.Ordinal);
    }

    private static async Task<string> ReadFailedStreamAsync(HttpClient client, HttpRequestMessage request, long failSequence,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // Kestrel may terminate a failed OnStarting callback with an empty 500 before
            // the capture middleware can render 503. Neither outcome may release an SSE event.
            Assert.IsTrue(failSequence == 1
                ? response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable
                : response.StatusCode == HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.InternalServerError)
                Assert.AreEqual(string.Empty, body, StringComparer.Ordinal);
            return body;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return string.Empty; // A failed durable start/chunk may abort before HTTP headers/body arrive.
        }
    }
}
