using System.Globalization;
using System.Net;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] FolderRestrictionAuthorityOutcomes = ["NoError", "ErrorAccessDenied", "ErrorFolderNotFound", "NoError"];
    private static readonly string[] FolderRestrictionDuplicateNames = ["Deep", "Deep"];

    [TestMethod]
    [DataRow("FullString", "Exact", "Deep", true, 1)]
    [DataRow("FullString", "Exact", "deep", true, 0)]
    [DataRow("FullString", "IgnoreCase", "deep", true, 1)]
    [DataRow("Prefixed", "Exact", "De", true, 1)]
    [DataRow("Substring", "IgnoreCase", "EE", true, 1)]
    [DataRow("FullString", "Exact", "Deep", false, 0)]
    [DataRow("FullString", "Exact", "INBOX", true, 0)]
    public async Task FolderRestrictionUsesLeafNameAndTraversesNonmatchingAncestors(
        string mode, string comparison, string name, bool deep, int expected)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FolderRestrictedRequest(FolderNameRestriction(mode, comparison, name), deep));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var root = XDocument.Parse(body).Descendants(Messages + "RootFolder").Single();
        Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), (string?)root.Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.AreEqual("true", (string?)root.Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
        Assert.HasCount(expected, root.Descendants(Types + "Folder"));
        if (expected != 0) Assert.AreEqual("Deep", root.Descendants(Types + "DisplayName").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(body.Contains("PRIVATE FOREIGN NAME", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("folder:TotalCount", "IsEqualTo", "3")]
    [DataRow("folder:UnreadCount", "IsGreaterThanOrEqualTo", "1")]
    [DataRow("folder:ChildFolderCount", "IsGreaterThan", "0")]
    public async Task FolderRestrictionUsesRealMessageAndImmediateChildCounts(string field, string comparison, string count)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FolderRestrictedRequest(FolderCountRestriction(comparison, field, count), deep: false,
            references: "<t:DistinguishedFolderId Id='msgfolderroot'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        var root = XDocument.Parse(body).Descendants(Messages + "RootFolder").Single();
        Assert.AreEqual("1", (string?)root.Attribute("TotalItemsInView"), StringComparer.Ordinal);
        var folder = root.Descendants(Types + "Folder").Single();
        Assert.AreEqual("INBOX", folder.Element(Types + "DisplayName")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("3", folder.Element(Types + "TotalCount")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("1", folder.Element(Types + "UnreadCount")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("1", folder.Element(Types + "ChildFolderCount")!.Value, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 0)]
    public async Task FolderRestrictionFiltersBeforeIndexedPagingAndReportsMatchingTotal(int offset, int expected)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var page = $"<m:IndexedPageFolderView BasePoint='Beginning' Offset='{offset.ToString(CultureInfo.InvariantCulture)}' MaxEntriesReturned='1'/>";
        using var content = XmlContent(FolderRestrictedRequest(FolderNameRestriction("FullString", "Exact", "Deep"), deep: true, page));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        var root = XDocument.Parse(body).Descendants(Messages + "RootFolder").Single();
        Assert.AreEqual("1", (string?)root.Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.AreEqual("1", (string?)root.Attribute("IndexedPagingOffset"), StringComparer.Ordinal);
        Assert.AreEqual("true", (string?)root.Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
        Assert.HasCount(expected, root.Descendants(Types + "Folder"));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FolderRestrictionNegationAndDisjunctionCannotBroadenScopeOrForeignAuthority()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var foreign = GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId);
        var forged = GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, GatewayEwsFixtureDomain.ForeignFolderId);
        var references = "<t:DistinguishedFolderId Id='inbox'/>" + $"<t:FolderId Id='{foreign}'/><t:FolderId Id='{forged}'/>"
            + "<t:DistinguishedFolderId Id='inbox'/>";
        var expression = "<t:And><t:Or>" + FolderNameRestriction("FullString", "Exact", "Deep")
            + FolderNameRestriction("FullString", "Exact", "PRIVATE FOREIGN NAME") + "</t:Or><t:Not>"
            + FolderNameRestriction("FullString", "Exact", "A &amp; B") + "</t:Not></t:And>";
        using var content = XmlContent(FolderRestrictedRequest(expression, deep: true, references: references));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        var document = XDocument.Parse(body);
        Assert.AreSequenceEqual(FolderRestrictionAuthorityOutcomes,
            document.Descendants(Messages + "ResponseCode").Select(code => code.Value), StringComparer.Ordinal);
        Assert.AreSequenceEqual(FolderRestrictionDuplicateNames, document.Descendants(Types + "DisplayName").Select(name => name.Value), StringComparer.Ordinal);
        Assert.IsFalse(body.Contains("PRIVATE FOREIGN NAME", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FolderRestrictionDoesNotEvadeWholeGraphAdmissionEvenWhenItMatchesOneRow()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: 494).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FolderRestrictedRequest(FolderNameRestriction("FullString", "Exact", "Deep"), deep: true));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", XDocument.Parse(body).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(XDocument.Parse(body).Descendants(Types + "Folder"));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<t:Exists><t:FieldURI FieldURI='folder:DisplayName'/></t:Exists>", "ErrorInvalidRequest")]
    [DataRow("<t:Contains ContainmentMode='Substring' ContainmentComparison='Loose'><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value='x'/></t:Contains>", "ErrorInvalidRequest")]
    [DataRow("<t:IsEqualTo><t:FieldURI FieldURI='folder:PermissionSet'/><t:FieldURIOrConstant><t:Constant Value='0'/></t:FieldURIOrConstant></t:IsEqualTo>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:Not/>", "ErrorSchemaValidation")]
    public async Task FolderRestrictionUnsupportedSyntaxRefusesBeforeAnyWorkerRequest(string expression, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FolderRestrictedRequest(expression, deep: true));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task FolderRestrictionRetainsInboundAndOutboundJournalRefusal(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FolderRestrictedRequest(FolderNameRestriction("FullString", "Exact", "Deep"), deep: true));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("Deep", StringComparison.Ordinal));
        Assert.AreEqual(sequence, (await fixture.Faults.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false)).Sequence);
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        else await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
    }

    private static string FolderRestrictedRequest(string expression, bool deep, string page = "",
        string references = "<t:DistinguishedFolderId Id='inbox'/>") => Request("FindFolder", references,
            page: page + "<m:Restriction>" + expression + "</m:Restriction>", deep: deep);
    private static string FolderNameRestriction(string mode, string comparison, string value) =>
        $"<t:Contains ContainmentMode='{mode}' ContainmentComparison='{comparison}'><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value='{value}'/></t:Contains>";
    private static string FolderCountRestriction(string comparison, string field, string value) =>
        $"<t:{comparison}><t:FieldURI FieldURI='{field}'/><t:FieldURIOrConstant><t:Constant Value='{value}'/></t:FieldURIOrConstant></t:{comparison}>";
}
