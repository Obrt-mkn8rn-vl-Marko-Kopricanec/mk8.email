using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this real-Kestrel/PostgreSQL/domain fixture; case discovery is retained.")]
internal sealed partial class GatewayEwsRouteTests
{
    private const string CanonicalPath = "/EWS/Exchange.asmx";
    private static readonly XNamespace Messages = GatewayEwsSoap.Messages;
    private static readonly XNamespace Types = GatewayEwsSoap.Types;
    private static readonly string[] MailRoles = ["msgfolderroot", "inbox", "sentitems", "drafts", "deleteditems", "junkemail", "calendar", "root"];
    private static readonly string[] RoleOutcomes = ["NoError", "NoError", "NoError", "NoError", "NoError", "NoError", "ErrorFolderNotFound", "ErrorFolderNotFound"];
    private static readonly string[] AuthorityOutcomes = ["ErrorAccessDenied", "ErrorAccessDenied", "ErrorAccessDenied", "ErrorFolderNotFound", "ErrorInvalidIdMalformed"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task FolderReadsUseRealOwnedDomainCountsAndOriginalDurableSessions(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        Authenticate(fixture);
        fixture.Client.DefaultRequestHeaders.Host = "localhost";
        using var content = XmlContent(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />"));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/xml", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var folder = XDocument.Parse(body).Descendants(Types + "Folder").Single();
        Assert.AreEqual("INBOX", folder.Element(Types + "DisplayName")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("3", folder.Element(Types + "TotalCount")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("1", folder.Element(Types + "UnreadCount")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("1", folder.Element(Types + "ChildFolderCount")!.Value, StringComparer.Ordinal);
        Assert.IsTrue(GatewayEwsFolderIdCodec.TryDecode((string)folder.Element(Types + "FolderId")!.Attribute("Id")!, out var account, out var id));
        Assert.AreEqual(GatewayEwsFixtureDomain.AccountId, account);
        Assert.AreEqual(GatewayEwsFixtureDomain.InboxId, id);
        Assert.IsFalse(body.Contains("PRIVATE FOREIGN NAME", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("test-protocol-secret", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", path, 200, body, rejection: false).ConfigureAwait(false);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DistinguishedMailRolesAndRootAreRealButUnsupportedStoresAreNotInvented()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(Request("GetFolder", string.Concat(MailRoles.Select(id => $"<t:DistinguishedFolderId Id='{id}' />"))));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var document = XDocument.Parse(body);
        CollectionAssert.AreEqual(RoleOutcomes,
            document.Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        Assert.AreEqual("5", document.Descendants(Types + "Folder").First().Element(Types + "ChildFolderCount")!.Value, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task XMLMailboxAndOpaqueForeignIdsCannotSupplyAuthority()
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var foreign = GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId);
        var forged = GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, GatewayEwsFixtureDomain.ForeignFolderId);
        var references = "<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>foreign@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>"
            + "<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>alias@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>"
            + $"<t:FolderId Id='{foreign}' /><t:FolderId Id='{forged}' /><t:FolderId Id='bad' />";
        using var content = XmlContent(Request("GetFolder", references));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(AuthorityOutcomes,
            XDocument.Parse(body).Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        Assert.IsFalse(body.Contains("PRIVATE FOREIGN NAME", StringComparison.Ordinal));
        Assert.IsFalse(XDocument.Parse(body).Descendants(Types + "Folder").Any());
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 2)]
    public async Task FindFolderReturnsShallowOrDeepDescendantsWithForwardPaging(bool deep, int expectedTotal)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var page = "<m:IndexedPageFolderView BasePoint='Beginning' Offset='0' MaxEntriesReturned='1' />";
        var xml = Request("FindFolder", "<t:DistinguishedFolderId Id='inbox' />", page: page, deep: deep);
        using var content = XmlContent(xml);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var root = XDocument.Parse(body).Descendants(Messages + "RootFolder").Single();
        Assert.AreEqual(expectedTotal.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)root.Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.AreEqual("1", (string?)root.Attribute("IndexedPagingOffset"), StringComparer.Ordinal);
        Assert.AreEqual(deep ? "false" : "true", (string?)root.Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
        Assert.AreEqual("A & B", root.Descendants(Types + "DisplayName").Single().Value, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        using var nextContent = XmlContent(Request("FindFolder", "<t:DistinguishedFolderId Id='inbox' />", page: page.Replace("Offset='0'", "Offset='1'", StringComparison.Ordinal), deep: deep));
        using var next = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), nextContent).ConfigureAwait(false);
        var nextBody = await next.Content.ReadAsStringAsync().ConfigureAwait(false);
        var nextRoot = XDocument.Parse(nextBody).Descendants(Messages + "RootFolder").Single();
        Assert.AreEqual("true", (string?)nextRoot.Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
        if (deep) Assert.AreEqual("Deep", nextRoot.Descendants(Types + "DisplayName").Single().Value, StringComparer.Ordinal);
        else Assert.IsFalse(nextRoot.Descendants(Types + "Folder").Any());
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, nextBody, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task JmapFolderRenameIsVisibleThroughTheSameStableEwsIdAndNewChangeKey()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var id = GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, GatewayEwsFixtureDomain.ChildId);
        var xml = Request("GetFolder", $"<t:FolderId Id='{id}' />");
        using var beforeContent = XmlContent(xml);
        using var before = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), beforeContent).ConfigureAwait(false);
        var beforeBody = await before.Content.ReadAsStringAsync().ConfigureAwait(false);
        var key = (string)XDocument.Parse(beforeBody).Descendants(Types + "FolderId").Single().Attribute("ChangeKey")!;
        var mutation = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail"),
            ["methodCalls"] = new JsonArray(new JsonArray("Mailbox/set", new JsonObject
            {
                ["accountId"] = $"A{GatewayEwsFixtureDomain.AccountId:N}",
                ["update"] = new JsonObject { [$"M{GatewayEwsFixtureDomain.ChildId:N}"] = new JsonObject { ["name"] = "Renamed <&>" } },
            }, "rename")),
        }.ToJsonString();
        using var mutationContent = new StringContent(mutation, Encoding.UTF8, "application/json");
        using var mutationResponse = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), mutationContent).ConfigureAwait(false);
        using var mutationDocument = JsonDocument.Parse(await mutationResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        var result = mutationDocument.RootElement.GetProperty("methodResponses")[0];
        Assert.AreEqual("Mailbox/set", result[0].GetString(), StringComparer.Ordinal);
        Assert.IsTrue(result[1].GetProperty("updated").TryGetProperty($"M{GatewayEwsFixtureDomain.ChildId:N}", out _));
        using var afterContent = XmlContent(xml);
        using var after = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), afterContent).ConfigureAwait(false);
        var body = await after.Content.ReadAsStringAsync().ConfigureAwait(false);
        var folder = XDocument.Parse(body).Descendants(Types + "Folder").Single();
        Assert.AreEqual("Renamed <&>", folder.Element(Types + "DisplayName")!.Value, StringComparer.Ordinal);
        Assert.AreEqual(id, (string?)folder.Element(Types + "FolderId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreNotEqual(key, (string?)folder.Element(Types + "FolderId")!.Attribute("ChangeKey"), StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(493, true)]
    [DataRow(494, false)]
    public async Task HierarchyBoundarySucceedsOrFailsWithoutPartialResults(int additional, bool accepted)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: additional).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(accepted ? "NoError" : "ErrorExceededFindCountLimit", XDocument.Parse(body).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.AreEqual(accepted, XDocument.Parse(body).Descendants(Types + "Folder").Any());
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        if (!accepted) await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(312, true)]
    [DataRow(313, false)]
    public async Task SmallEnvelopeWithJmapHttpDisabledHonorsTheEncodedSnapshotBoundary(int additional, bool accepted)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: additional,
            maximumPayloadOverride: 1_572_864, disableJmap: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(accepted ? "NoError" : "ErrorExceededFindCountLimit",
            XDocument.Parse(body).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.AreEqual(accepted, XDocument.Parse(body).Descendants(Types + "Folder").Any());
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        if (accepted) await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
        else await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LargeBatchedSoapReplyIsARecordedSizeFaultNotAStorageFailure()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: 312,
            maximumPayloadOverride: 1_572_864, disableJmap: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var references = string.Concat(Enumerable.Repeat("<t:DistinguishedFolderId Id='msgfolderroot' />", 32));
        var xml = Request("FindFolder", references,
            page: "<m:IndexedPageFolderView BasePoint='Beginning' Offset='0' MaxEntriesReturned='100' />");
        using var withinContent = XmlContent(xml);
        using var withinResponse = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), withinContent).ConfigureAwait(false);
        var withinBody = await withinResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, withinResponse.StatusCode);
        Assert.IsLessThanOrEqualTo(1_572_864L, GatewayHttpPayloadBudget.BinaryEnvelopeBytes(Encoding.UTF8.GetByteCount(withinBody)));
        Assert.IsTrue(XDocument.Parse(withinBody).Descendants(Messages + "ResponseCode").All(code => string.Equals(code.Value, "NoError", StringComparison.Ordinal)));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, withinBody, rejection: false).ConfigureAwait(false);
        xml = xml.Replace("</t:BaseShape>", "</t:BaseShape><t:AdditionalProperties>"
            + "<t:FieldURI FieldURI='folder:ParentFolderId' /><t:FieldURI FieldURI='folder:FolderClass' />"
            + "</t:AdditionalProperties>", StringComparison.Ordinal);
        using var content = XmlContent(xml);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, "ErrorDataSizeLimitExceeded");
        Assert.IsFalse(body.Contains("Extra-", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute, ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("none", 401)]
    [DataRow("bearer", 401)]
    [DataRow("http", 403)]
    public async Task MissingUnsupportedOrNonTlsCredentialsDoNotReachWorker(string mode, int status)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        if (mode is "none") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "bearer") fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only-token");
        if (mode is "http") fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        using var content = XmlContent(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        AssertFault(body, "ErrorAccessDenied");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, status, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RejectedActualDomainAuthenticationReturnsOnlyABasicChallenge()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture, "wrong");
        using var content = XmlContent(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("Basic", response.Headers.WwwAuthenticate.Single().Scheme, StringComparer.Ordinal);
        AssertFault(body, "ErrorAccessDenied");
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 401, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("X-Test-Failure", 500, "ErrorInternalServerError")]
    [DataRow("X-Test-Unavailable", 503, "ErrorServerBusy")]
    public async Task AliasFailuresAreSanitizedSoapWithinTheJournal(string header, int status, string code)
    {
        const string path = "/eWs/EXCHANGE.ASMX/";
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(header, "true");
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        AssertFault(body, code);
        Assert.IsFalse(body.Contains("deliberate secret exception", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", path, status, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KnownOrIncompleteChunkedLimitsRejectEarlyWithoutWorker(bool chunked)
    {
        const string path = "/eWs/EXCHANGE.ASMX/";
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        string body;
        if (!chunked)
        {
            using var content = new ByteArrayContent(new byte[65_537]);
            using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        else
        {
            using var connection = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.ConnectAsync(fixture.Address.Host, fixture.Address.Port, deadline.Token).ConfigureAwait(false);
            var stream = connection.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\nX-Forwarded-Proto: https\r\nContent-Type: text/xml\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n10001\r\n"), deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[65_537], deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            // No CRLF/chunk terminator: an unbounded reader would wait here.
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            StringAssert.Contains((await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false))!, "413", StringComparison.Ordinal);
            var length = 0;
            while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { Length: > 0 } line)
                if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.AsSpan(16), System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(0, length);
            var characters = new char[length];
            Assert.AreEqual(length, await reader.ReadBlockAsync(characters.AsMemory(), deadline.Token).ConfigureAwait(false));
            body = new string(characters);
        }
        AssertFault(body, "ErrorDataSizeLimitExceeded");
        Assert.AreEqual(0, fixture.EndpointCalls);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", path, 413, body, rejection: true).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task JournalFailureWithholdsFolderData(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("INBOX", StringComparison.Ordinal));
        Assert.IsFalse(XDocument.Parse(body).Descendants(Types + "Folder").Any());
        Assert.AreEqual(sequence, (await fixture.Faults.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Sequence);
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        else await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("before")]
    [DataRow("after")]
    [DataRow("shape")]
    [DataRow("address")]
    [DataRow("optional")]
    [DataRow("other-actor")]
    public async Task ProcessingInstructionsAreJournaledRefusalsWithoutWorkerDispatch(string location)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        const string instruction = "<?opaque inert?>";
        var header = location is "optional" or "other-actor"
            ? $"<t:Unknown s:mustUnderstand='0'{(location is "other-actor" ? " s:actor='urn:other'" : "")}><t:Nested>{instruction}</t:Nested></t:Unknown>" : "";
        var xml = Request("GetFolder", $"<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>owner@example.test{(location is "address" ? instruction : "")}</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>", header: header);
        if (location is "shape") xml = xml.Replace("Default", $"Default{instruction}", StringComparison.Ordinal);
        if (location is "before") xml = instruction + xml;
        if (location is "after") xml += instruction;
        using var content = XmlContent(xml);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, "ErrorSchemaValidation");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("DTD", "ErrorInvalidRequest")]
    [DataRow("impersonation", "ErrorAccessDenied")]
    [DataRow("action", "ErrorInvalidRequest")]
    [DataRow("charset", "ErrorSchemaValidation")]
    public async Task RejectedSoapCannotDispatchBusinessLogic(string mode, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var xml = mode is "DTD" ? "<!DOCTYPE x SYSTEM 'file:///never-mk8-secret'><x />"
            : Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />", header: mode is "impersonation" ? "<t:ExchangeImpersonation />" : "");
        using var content = XmlContent(xml);
        if (mode is "charset") content.Headers.ContentType!.CharSet = "utf-16";
        using var request = new HttpRequestMessage(HttpMethod.Post, CanonicalPath) { Content = content };
        if (mode is "action") request.Headers.Add("SOAPAction", "\"urn:foreign-operation\"");
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    private static Task AssertReadOperationsAsync(CaptureFixture fixture) => fixture.AssertWorkerOperationsAsync(
        ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute);

    private static void Authenticate(CaptureFixture fixture, string password = "test-protocol-secret") =>
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"OWNER@EXAMPLE.TEST:{password}")));

    private static void AssertFault(string xml, string code)
    {
        var fault = XDocument.Parse(xml).Descendants(GatewayEwsSoap.Soap + "Fault").Single();
        Assert.AreEqual($"t:{code}", fault.Element("faultcode")!.Value, StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("ADMIN ERROR HTML", StringComparison.Ordinal));
    }

    private static StringContent XmlContent(string xml) => new(xml, Encoding.UTF8, "text/xml");

    private static string Request(string operation, string references, string page = "", bool deep = false, string header = "")
    {
        var find = string.Equals(operation, "FindFolder", StringComparison.Ordinal);
        var field = find ? "ParentFolderIds" : "FolderIds";
        return $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'>"
            + (header.Length == 0 ? "" : $"<s:Header>{header}</s:Header>") + $"<s:Body><m:{operation}"
            + (find ? $" Traversal='{(deep ? "Deep" : "Shallow")}'" : "")
            + $"><m:FolderShape><t:BaseShape>Default</t:BaseShape></m:FolderShape>{page}<m:{field}>{references}</m:{field}></m:{operation}></s:Body></s:Envelope>";
    }
}
