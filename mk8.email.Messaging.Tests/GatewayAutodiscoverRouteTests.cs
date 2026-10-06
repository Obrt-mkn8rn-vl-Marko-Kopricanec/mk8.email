using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Autodiscover;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this real-Kestrel integration class; retained executed discovery verifies its cases.")]
internal sealed class GatewayAutodiscoverRouteTests
{
    private const string CanonicalPath = "/autodiscover/autodiscover.xml";

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow("/AutoDiscover/AutoDiscover.XML/", true)]
    public async Task AccountDiscoveryCrossesTypedWorkerBoundaryAndIsDurablyRecorded(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, discoveryListeners: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        Authenticate(fixture);
        // This accepted HTTP host is intentionally different from configured Smtp.Hostname.
        // The framework's separate AllowedHosts policy still rejects unlisted hosts.
        fixture.Client.DefaultRequestHeaders.Host = "localhost";
        using var content = XmlContent();
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/xml", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        XNamespace ns = GatewayAutodiscoverXml.ResponseNamespace;
        var document = XDocument.Parse(body);
        CollectionAssert.AreEqual(ExpectedTypes, document.Descendants(ns + "Type").Select(element => element.Value).ToArray());
        CollectionAssert.AreEqual(ExpectedPorts, document.Descendants(ns + "Port").Select(element => element.Value).ToArray());
        Assert.IsTrue(document.Descendants(ns + "Server").All(element => string.Equals(element.Value, "email.example.test", StringComparison.Ordinal)));
        Assert.IsTrue(document.Descendants(ns + "LoginName").All(element => string.Equals(element.Value, "owner@example.test", StringComparison.Ordinal)));
        Assert.IsFalse(body.Contains("test-protocol-secret", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("autodiscover", path, 200, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.ImapAuthenticatePassword, ApplicationOperations.ImapListMailboxes).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("other@example.test", "owner@example.test", 500, 1)]
    [DataRow("alias@example.test", "owner@example.test", 500, 1)]
    [DataRow("empty@example.test", "empty@example.test", 500, 2)]
    [DataRow("owner@example.test", "bad@example.test", 500, 1)]
    public async Task RequestedAccountCannotSupplyAuthority(string address, string login, int expectedCode, int operations)
    {
        var fixture = await CaptureFixture.CreateAsync(discoveryListeners: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture, login);
        using var content = XmlContent(address);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertError(body, expectedCode);
        Assert.IsFalse(body.Contains("email.example.test", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, (int)response.StatusCode, body, rejection: false).ConfigureAwait(false);
        if (operations == 1)
            await fixture.AssertWorkerOperationsAsync(ApplicationOperations.ImapAuthenticatePassword).ConfigureAwait(false);
        else
            await fixture.AssertWorkerOperationsAsync(ApplicationOperations.ImapAuthenticatePassword, ApplicationOperations.ImapListMailboxes).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnauthenticatedOrPlainHttpDoesNotDispatchCredentials(bool plainHttp)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        if (plainHttp)
            fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        else
            fixture.Client.DefaultRequestHeaders.Authorization = null;
        using var content = XmlContent();
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(plainHttp ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        AssertError(body, plainHttp ? 600 : 500);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, (int)response.StatusCode, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("X-Test-Failure", 500)]
    [DataRow("X-Test-Unavailable", 503)]
    public async Task AliasFailuresUseSanitizedJournaledXml(string header, int status)
    {
        const string path = "/AutoDiscover/AutoDiscover.XML/";
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(header, "true");
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        AssertError(body, 603);
        Assert.IsFalse(body.Contains("deliberate secret exception", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("autodiscover", path, status, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task KnownLengthLimitRejectionIsXmlAndPrecedesWorkerDispatch()
    {
        const string path = "/AutoDiscover/AutoDiscover.XML/";
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var content = new ByteArrayContent(new byte[65_537]);
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertError(body, 600);
        Assert.AreEqual(0, fixture.EndpointCalls);
        await fixture.AssertRecordedResponseAsync("autodiscover", path, 413, body, rejection: true).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DisabledMailListenersProduceNoInventedConfiguration()
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent();
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertError(body, 501);
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InvalidXmlDoesNotCallWorker()
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var content = new StringContent("<!DOCTYPE x SYSTEM 'file:///never-mk8-secret'><x />", Encoding.UTF8, "text/xml");
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertError(body, 600);
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task JournalFailureWithholdsSettingsAndFailsClosed(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, failSequence: sequence, discoveryListeners: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent();
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertError(body, 603);
        Assert.IsFalse(body.Contains("email.example.test", StringComparison.Ordinal));
        var failed = await fixture.Faults.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(sequence, failed.Sequence);
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        if (sequence == 0)
            await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        else
            await fixture.AssertWorkerOperationsAsync(ApplicationOperations.ImapAuthenticatePassword, ApplicationOperations.ImapListMailboxes).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ChunkedLimitRejectionDoesNotWaitForTerminatorOrDispatchWorker()
    {
        const string path = "/AutoDiscover/AutoDiscover.XML/";
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var connection = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await connection.ConnectAsync(fixture.Address.Host, fixture.Address.Port, deadline.Token).ConfigureAwait(false);
        var stream = connection.GetStream();
        var headers = Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\n"
            + "X-Forwarded-Proto: https\r\nContent-Type: text/xml\r\nTransfer-Encoding: chunked\r\n"
            + "Connection: close\r\n\r\n10001\r\n");
        await stream.WriteAsync(headers, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[65_537], deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        // No chunk terminator is sent. Reading the complete request would hang.
        var body = await ReadEarlyRejectionAsync(stream, deadline.Token).ConfigureAwait(false);
        AssertError(body, 600);
        await fixture.AssertRecordedResponseAsync("autodiscover", path, 413, body, rejection: true).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("HEAD")]
    public async Task NonPostDiscoveryIsRecordedWithoutWorkerOrHeadBody(string method)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod(method), CanonicalPath);
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        if (string.Equals(method, "HEAD", StringComparison.Ordinal))
            Assert.AreEqual(string.Empty, body, StringComparer.Ordinal);
        else
            AssertError(body, 600);
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("urn:unsupported")]
    [DataRow("")]
    public async Task SchemaFailurePrecedesWorkerDispatch(string schema)
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(schema: schema);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertError(body, 601);
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("Bearer", "test-only-token")]
    [DataRow("Basic", "!!!")]
    [DataRow("Basic", "b3duZXJAZXhhbXBsZS50ZXN0Og==")]
    public async Task UnsupportedOrMalformedCredentialsDoNotReachWorker(string scheme, string parameter)
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, parameter);
        using var content = XmlContent();
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertError(body, 500);
        Assert.AreEqual("Basic", response.Headers.WwwAuthenticate.Single().Scheme, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("autodiscover", CanonicalPath, 401, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    private static async Task<string> ReadEarlyRejectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var status = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        StringAssert.Contains(status!, "413", StringComparison.Ordinal);
        var length = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
            if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line.AsSpan(16), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(0, length);
        var characters = new char[length];
        Assert.AreEqual(length, await reader.ReadBlockAsync(characters.AsMemory(), cancellationToken).ConfigureAwait(false));
        return new string(characters);
    }

    private static void Authenticate(CaptureFixture fixture, string username = "owner@example.test") =>
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":test-protocol-secret")));

    private static StringContent XmlContent(string address = "owner@example.test", string schema = GatewayAutodiscoverXml.ResponseNamespace)
    {
        XNamespace ns = GatewayAutodiscoverXml.RequestNamespace;
        var xml = new XElement(ns + "Autodiscover", new XElement(ns + "Request",
            new XElement(ns + "EMailAddress", address),
            new XElement(ns + "AcceptableResponseSchema", schema)));
        return new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
    }

    private static void AssertError(string body, int code)
    {
        var document = XDocument.Parse(body);
        var value = document.Descendants().Single(element => string.Equals(element.Name.LocalName, "ErrorCode", StringComparison.Ordinal)).Value;
        Assert.AreEqual(code.ToString(System.Globalization.CultureInfo.InvariantCulture), value, StringComparer.Ordinal);
        Assert.IsFalse(document.Descendants().Any(element => string.Equals(element.Name.LocalName, "Protocol", StringComparison.Ordinal)));
    }

    private static readonly string[] ExpectedTypes = ["IMAP", "POP3", "SMTP"];
    private static readonly string[] ExpectedPorts = ["993", "995", "465"];
}
