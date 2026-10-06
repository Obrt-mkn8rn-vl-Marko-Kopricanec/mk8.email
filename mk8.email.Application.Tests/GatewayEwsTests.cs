using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals creates this fixture; executed standards-vector discovery is retained.")]
internal sealed class GatewayEwsTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Parent = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Child = new("1438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Grandchild = new("1538f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly string[] DefaultProperties = ["FolderId", "DisplayName", "TotalCount", "ChildFolderCount", "UnreadCount"];
    private static readonly string[] OrderedProperties = ["FolderId", "ParentFolderId", "FolderClass", "DisplayName", "TotalCount", "ChildFolderCount", "UnreadCount"];

    [TestMethod]
    [DataRow("utf-8")]
    [DataRow("utf-16")]
    [DataRow("utf-16be")]
    public async Task SoapEncodingAndNamespacePrefixesFollowTheRegisteredRequest(string name)
    {
        Encoding encoding = name switch { "utf-16" => new UnicodeEncoding(false, true, true), "utf-16be" => new UnicodeEncoding(true, true, true), _ => new UTF8Encoding(true, true) };
        var xml = $"<?xml version='1.0' encoding='{name}'?>" + Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />");
        using var body = new MemoryStream(encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray());
        var parsed = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None, name).ConfigureAwait(false);
        Assert.AreEqual("GetFolder", parsed.Operation, StringComparer.Ordinal);
        Assert.AreEqual("inbox", parsed.Folders[0].Id, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(DefaultProperties, parsed.Properties.ToArray());
    }

    [TestMethod]
    [DataRow("CreateFolder", "<m:ParentFolderId><t:DistinguishedFolderId Id='msgfolderroot'/></m:ParentFolderId><m:Folders><t:Folder><t:DisplayName>A &amp; B</t:DisplayName></t:Folder></m:Folders>", "A & B")]
    [DataRow("UpdateFolder", "<m:FolderChanges><t:FolderChange><t:FolderId Id='opaque' ChangeKey='czE='/><t:Updates><t:SetFolderField><t:FieldURI FieldURI='folder:DisplayName'/><t:Folder><t:DisplayName>New</t:DisplayName></t:Folder></t:SetFolderField></t:Updates></t:FolderChange></m:FolderChanges>", "New")]
    [DataRow("DeleteFolder", "<m:FolderIds><t:FolderId Id='opaque'/></m:FolderIds>", null)]
    public async Task MutationShapesUseRegisteredElementOrderAndKeepVersionReferences(string operation, string fields, string? name)
    {
        var xml = Mutation(operation, fields);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.IsMutation);
        Assert.AreEqual(operation, result.Operation, StringComparer.Ordinal);
        Assert.AreEqual(name, result.Names!.Count == 0 ? null : result.Names[0], StringComparer.Ordinal);
        if (operation is "UpdateFolder") Assert.AreEqual("czE=", result.Folders[0].ChangeKey, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("CreateFolder", "<m:ParentFolderId><t:DistinguishedFolderId Id='msgfolderroot'/></m:ParentFolderId><m:Folders><t:CalendarFolder><t:DisplayName>Calendar</t:DisplayName></t:CalendarFolder></m:Folders>", "ErrorInvalidPropertySet")]
    [DataRow("CreateFolder", "<m:ParentFolderId><t:DistinguishedFolderId Id='msgfolderroot'/></m:ParentFolderId><m:Folders><t:Folder><t:DisplayName>Invalid/path</t:DisplayName></t:Folder></m:Folders>", "ErrorInvalidFolderName")]
    [DataRow("CreateFolder", "<m:Folders/><m:ParentFolderId/>", "ErrorSchemaValidation")]
    [DataRow("UpdateFolder", "<m:FolderChanges><t:FolderChange><t:FolderId Id='opaque'/><t:Updates><t:DeleteFolderField><t:FieldURI FieldURI='folder:DisplayName'/></t:DeleteFolderField></t:Updates></t:FolderChange></m:FolderChanges>", "ErrorInvalidPropertySet")]
    [DataRow("DeleteFolder", "<m:FolderIds><t:FolderId Id='same'/><t:FolderId Id='same'/></m:FolderIds>", "ErrorInvalidRequest")]
    public async Task UnsupportedOrAmbiguousMutationShapesDoNotBecomeCommands(string operation, string fields, string code)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Mutation(operation, fields)));
        var exception = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(code, exception.Code, StringComparer.Ordinal);
    }

    private static string Mutation(string operation, string fields) =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:{operation}{(operation is "DeleteFolder" ? " DeleteType='HardDelete'" : "")}>{fields}</m:{operation}></s:Body></s:Envelope>";

    [TestMethod]
    [DataRow("before")]
    [DataRow("after")]
    [DataRow("shape")]
    [DataRow("address")]
    [DataRow("optional")]
    [DataRow("other-actor")]
    public async Task ProcessingInstructionsAreForbiddenThroughoutTheDocument(string location)
    {
        const string instruction = "<?opaque inert?>";
        var header = location is "optional" or "other-actor"
            ? $"<t:Unknown s:mustUnderstand='0'{(location is "other-actor" ? " s:actor='urn:other'" : "")}><t:Nested>{instruction}</t:Nested></t:Unknown>" : "";
        var xml = Request("GetFolder", $"<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>owner@example.test{(location is "address" ? instruction : "")}</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>", header: header);
        if (location is "shape") xml = xml.Replace("Default", $"Default{instruction}", StringComparison.Ordinal);
        if (location is "before") xml = instruction + xml;
        if (location is "after") xml += instruction;
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorSchemaValidation", error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<broken>", "ErrorInvalidRequest")]
    [DataRow("<!DOCTYPE x SYSTEM 'file:///never-mk8-secret'><x />", "ErrorInvalidRequest")]
    [DataRow("<Envelope xmlns='urn:wrong'><Body /></Envelope>", "VersionMismatch")]
    [DataRow("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body /><s:Body /></s:Envelope>", "ErrorSchemaValidation")]
    public async Task InvalidSoapCannotBecomeAWorkerCommand(string xml, string expected)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(expected, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:RequestServerVersion Version='Exchange2013_SP1' s:mustUnderstand='1' />", null)]
    [DataRow("<t:RequestServerVersion Version='pretend' />", "ErrorInvalidServerVersion")]
    [DataRow("<t:Unknown s:mustUnderstand='1' />", "MustUnderstand")]
    [DataRow("<t:Unknown s:mustUnderstand='0'><t:Opaque /></t:Unknown>", null)]
    [DataRow("<t:ExchangeImpersonation><t:ConnectingSID /></t:ExchangeImpersonation>", "ErrorAccessDenied")]
    [DataRow("<t:RequestServerVersion Version='Exchange2013' /><t:RequestServerVersion Version='Exchange2013' />", "ErrorSchemaValidation")]
    public async Task HeadersDoNotGrantImpersonationOrSilentlyIgnoreMandatorySemantics(string header, string? expected)
    {
        var xml = Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />", header: header);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        if (expected is null)
            Assert.AreEqual("GetFolder", (await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).Operation, StringComparer.Ordinal);
        else
            Assert.AreEqual(expected, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:FolderId Id='x'><t:FolderId Id='y' /></t:FolderId>")]
    [DataRow("<t:DistinguishedFolderId Id='inbox'><t:Mailbox /><t:Mailbox /></t:DistinguishedFolderId>")]
    [DataRow("<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>owner@example.test</t:EmailAddress><t:EmailAddress>foreign@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>")]
    [DataRow("<t:DistinguishedFolderId Id='inbox' extra='yes' />")]
    [DataRow("<t:DistinguishedFolderId xmlns:t='urn:wrong' Id='inbox' />")]
    [DataRow("<t:DistinguishedFolderId Id='invented' />")]
    public async Task DuplicateAndNonregisteredReferenceShapesAreRejected(string reference)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request("GetFolder", reference)));
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("AllProperties", "", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:AdditionalProperties><t:FieldURI FieldURI='folder:PermissionSet' /></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("Default", "<t:AdditionalProperties><t:ExtendedFieldURI PropertyTag='0x3001' PropertyType='String' /></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    public async Task UnsupportedPropertiesAreNotInventedOrSilentlyDropped(string shape, string additional, string expected)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />", shape: shape, additional: additional)));
        Assert.AreEqual(expected, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("End", "0", "10", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Beginning", "-1", "10", "ErrorInvalidIndexedPagingParameters")]
    [DataRow("Beginning", "0", "0", "ErrorInvalidIndexedPagingParameters")]
    public async Task UnsupportedOrInvalidPagingIsExplicit(string point, string offset, string limit, string expected)
    {
        var page = $"<m:IndexedPageFolderView BasePoint='{point}' Offset='{offset}' MaxEntriesReturned='{limit}' />";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request("FindFolder", "<t:DistinguishedFolderId Id='msgfolderroot' />", page: page)));
        Assert.AreEqual(expected, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EncodingContradictionsInvalidBytesAndOversizedReferencesAreRejected()
    {
        var xml = Request("GetFolder", "<t:DistinguishedFolderId Id='inbox' />");
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None, "utf-16")).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        using var invalid = new MemoryStream([0xff]);
        Assert.AreEqual("ErrorInvalidRequest", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(invalid, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        using var many = new MemoryStream(Encoding.UTF8.GetBytes(Request("GetFolder", string.Concat(Enumerable.Repeat("<t:DistinguishedFolderId Id='inbox' />", 33)))));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(many, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void OpaqueIdentifiersAreCanonicalScopedAndStable()
    {
        var id = GatewayEwsFolderIdCodec.Encode(Account, Child);
        Assert.IsTrue(GatewayEwsFolderIdCodec.TryDecode(id, out var account, out var folder));
        Assert.AreEqual(Account, account);
        Assert.AreEqual(Child, folder);
        Assert.IsFalse(GatewayEwsFolderIdCodec.TryDecode(id[..^1] + "\n", out _, out _));
        Assert.IsFalse(GatewayEwsFolderIdCodec.TryDecode(GatewayEwsFolderIdCodec.Encode(Guid.Empty, Child), out _, out _));
        Assert.IsTrue(GatewayEwsFolderIdCodec.TryDecode(GatewayEwsFolderIdCodec.Encode(Account, Guid.Empty), out _, out var root));
        Assert.AreEqual(Guid.Empty, root);
    }

    [TestMethod]
    public void HierarchyRenderingUsesOrderedRegisteredPropertiesAndEscapesLeafNames()
    {
        var folders = Folders();
        GatewayEwsFolderGraph.Validate(folders);
        var graph = new GatewayEwsFolderGraph(Account, "state-1", folders);
        var properties = new HashSet<string>(StringComparer.Ordinal) { "FolderId", "ParentFolderId", "FolderClass", "DisplayName", "TotalCount", "ChildFolderCount", "UnreadCount" };
        var element = graph.Render(Child, properties);
        CollectionAssert.AreEqual(OrderedProperties, element.Elements().Select(child => child.Name.LocalName).ToArray());
        Assert.AreEqual("A & B", element.Element(GatewayEwsSoap.Types + "DisplayName")!.Value, StringComparer.Ordinal);
        StringAssert.Contains(element.ToString(), "A &amp; B", StringComparison.Ordinal);
        CollectionAssert.AreEquivalent(new[] { Parent, Child, Grandchild }, graph.Find(Guid.Empty, true).ToArray());
        CollectionAssert.AreEqual(new[] { Child }, graph.Find(Parent, false).ToArray());
        CollectionAssert.AreEqual(new[] { Child, Grandchild }, graph.Find(Parent, true).ToArray());
        Assert.AreEqual("ErrorAccessDenied", graph.Resolve(new("inbox", true, "foreign@example.test"), "owner@example.test").Error, StringComparer.Ordinal);
        Assert.AreEqual("ErrorAccessDenied", graph.Resolve(new(GatewayEwsFolderIdCodec.Encode(Guid.CreateVersion7(), Child), false, null), "owner@example.test").Error, StringComparer.Ordinal);
    }

    [TestMethod]
    public void MalformedDomainGraphsCannotBePresented()
    {
        Assert.Throws<InvalidOperationException>(() => GatewayEwsFolderGraph.Validate([Snapshot(Parent, "a", Child), Snapshot(Child, "b", Parent)]));
        Assert.Throws<InvalidOperationException>(() => GatewayEwsFolderGraph.Validate([Snapshot(Parent, "a", Grandchild)]));
        Assert.Throws<InvalidOperationException>(() => GatewayEwsFolderGraph.Validate([Snapshot(Parent, "a", null), Snapshot(Parent, "b", null)]));
        Assert.Throws<InvalidOperationException>(() => GatewayEwsFolderGraph.Validate([Snapshot(Parent, "a", null) with { TotalEmails = -1 }]));
    }

    [TestMethod]
    public void PagingAndMixedBatchOutcomesHaveNativeEwsShape()
    {
        var graph = new GatewayEwsFolderGraph(Account, "state", Folders());
        var request = new GatewayEwsRequest("FindFolder", new HashSet<string>(StringComparer.Ordinal) { "FolderId" },
            [new("msgfolderroot", true, null), new("calendar", true, null)], true, 1, 1, true);
        var document = XDocument.Parse(GatewayEwsFolderResponse.Render(request, graph, "owner@example.test"));
        var responses = document.Descendants(GatewayEwsSoap.Messages + "FindFolderResponseMessage").ToArray();
        Assert.AreEqual("Success", (string?)responses[0].Attribute("ResponseClass"), StringComparer.Ordinal);
        Assert.AreEqual("Error", (string?)responses[1].Attribute("ResponseClass"), StringComparer.Ordinal);
        var root = responses[0].Element(GatewayEwsSoap.Messages + "RootFolder")!;
        Assert.AreEqual("3", (string?)root.Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.AreEqual("2", (string?)root.Attribute("IndexedPagingOffset"), StringComparer.Ordinal);
        Assert.AreEqual("false", (string?)root.Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
        Assert.HasCount(1, root.Descendants(GatewayEwsSoap.Types + "Folder"));
    }

    [TestMethod]
    public void UnpagedOverflowIsAnErrorInsteadOfAnUnpageablePartialHierarchy()
    {
        var graph = new GatewayEwsFolderGraph(Account, "state", Folders());
        var request = new GatewayEwsRequest("FindFolder", new HashSet<string>(StringComparer.Ordinal) { "FolderId" },
            [new("msgfolderroot", true, null)], true, 0, 1, false);
        var response = XDocument.Parse(GatewayEwsFolderResponse.Render(request, graph, "owner@example.test"));
        Assert.AreEqual("ErrorExceededFindCountLimit", response.Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(response.Descendants(GatewayEwsSoap.Types + "Folder").Any());
    }

    [TestMethod]
    public void HeaderFaultsDoNotClaimBodyProcessingThroughSoapDetail()
    {
        var fault = XDocument.Parse(GatewayEwsSoap.Fault("ErrorInvalidServerVersion", "unsupported", isHeaderFault: true));
        Assert.IsFalse(fault.Descendants("detail").Any());
        var bodyFault = XDocument.Parse(GatewayEwsSoap.Fault("ErrorInvalidRequest", "invalid"));
        Assert.IsTrue(bodyFault.Descendants("detail").Any());
    }

    [TestMethod]
    [DataRow("/EWS/Exchange.asmx")]
    [DataRow("/eWs/EXCHANGE.asmx/")]
    public void EwsAliasesShareTheProtocolBoundary(string path)
    {
        Assert.AreEqual("ews", GatewayProtocolPaths.GetProtocol(path), StringComparer.Ordinal);
        Assert.IsTrue(GatewayProtocolPaths.IsPublicProtocol(path));
        Assert.IsFalse(GatewayProtocolPaths.IsStreaming(path));
        Assert.IsNull(GatewayProtocolPaths.GetProtocol("/ewsevil/exchange.asmx"));
    }

    [TestMethod]
    public async Task StandaloneParserStopsAtItsBudgetPlusOneWithoutReadingTheRemainder()
    {
        using var stream = new MeasuredStream(new byte[100_000]);
        var rejection = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(413, rejection.Status);
        Assert.AreEqual(65_537L, stream.Position);
        Assert.IsLessThanOrEqualTo(16_384, stream.MaximumRead);
    }

    private sealed class MeasuredStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int MaximumRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static MailFolderSnapshot[] Folders() => [Snapshot(Parent, "INBOX", null) with { Role = "inbox" }, Snapshot(Child, "A & B", Parent), Snapshot(Grandchild, "Deep", Child)];
    private static MailFolderSnapshot Snapshot(Guid id, string name, Guid? parent) => new(id, name, parent, null, 0, true, 2, 1, 2, 1, false);

    internal static string Request(string operation, string references, string shape = "Default", string additional = "", string page = "", string header = "")
    {
        var find = string.Equals(operation, "FindFolder", StringComparison.Ordinal);
        var field = find ? "ParentFolderIds" : "FolderIds";
        return $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'>"
            + (header.Length == 0 ? "" : $"<s:Header>{header}</s:Header>") + $"<s:Body><m:{operation}"
            + (find ? " Traversal='Shallow'" : "") + $"><m:FolderShape><t:BaseShape>{shape}</t:BaseShape>{additional}</m:FolderShape>{page}"
            + $"<m:{field}>{references}</m:{field}></m:{operation}></s:Body></s:Envelope>";
    }
}
