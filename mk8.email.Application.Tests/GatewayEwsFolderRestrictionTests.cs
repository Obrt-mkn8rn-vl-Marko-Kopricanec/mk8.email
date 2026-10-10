using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this fixture; focused executed discovery retains all restriction rows.")]
internal sealed class GatewayEwsFolderRestrictionTests
{
    private static readonly Guid Account = new(0x1138f53b, 0x3efa, 0x4a59, 0x86, 0x02, 0xe2, 0x15, 0xc0, 0xdf, 0x1e, 0x35);
    private static readonly Guid Parent = new(0x1338f53b, 0x3efa, 0x4a59, 0x86, 0x02, 0xe2, 0x15, 0xc0, 0xdf, 0x1e, 0x35);
    private static readonly Guid Child = new(0x1438f53b, 0x3efa, 0x4a59, 0x86, 0x02, 0xe2, 0x15, 0xc0, 0xdf, 0x1e, 0x35);
    private static readonly Guid Descendant = new(0x1538f53b, 0x3efa, 0x4a59, 0x86, 0x02, 0xe2, 0x15, 0xc0, 0xdf, 0x1e, 0x35);
    private static readonly Guid Outside = new(0x1638f53b, 0x3efa, 0x4a59, 0x86, 0x02, 0xe2, 0x15, 0xc0, 0xdf, 0x1e, 0x35);

    [TestMethod]
    [DataRow("FullString", "Exact", "A &amp; B", true)]
    [DataRow("FullString", "Exact", "a &amp; b", false)]
    [DataRow("FullString", "IgnoreCase", "a &amp; b", true)]
    [DataRow("Prefixed", "Exact", "A &amp;", true)]
    [DataRow("Prefixed", "Exact", "a &amp;", false)]
    [DataRow("Prefixed", "IgnoreCase", "a &amp;", true)]
    [DataRow("Substring", "Exact", " &amp; ", true)]
    [DataRow("Substring", "Exact", "b", false)]
    [DataRow("Substring", "IgnoreCase", "b", true)]
    [DataRow("FullString", "IgnoreCase", " A &amp; B ", false)]
    public void NamePredicatesKeepDeclaredOrdinalModeAndDecodedConstant(string mode, string comparison, string value, bool expected)
    {
        var restriction = Parse(Contains(mode, comparison, value));
        Assert.AreEqual(expected, restriction.Matches(Snapshot(Child, "A & B", Parent), childCount: 1));
    }

    [TestMethod]
    [DataRow("Exact", "Café", true)]
    [DataRow("IgnoreCase", "CAFÉ", true)]
    [DataRow("IgnoreCase", "Cafe", false)]
    [DataRow("IgnoreCase", "Café", false)]
    public void NameComparisonDoesNotInventAccentOrNormalizationFolding(string comparison, string value, bool expected)
    {
        Assert.AreEqual(expected, Parse(Contains("FullString", comparison, value))
            .Matches(Snapshot(Child, "Café", Parent), childCount: 0));
    }

    [TestMethod]
    [DataRow("IsEqualTo", "2", true)]
    [DataRow("IsNotEqualTo", "2", false)]
    [DataRow("IsGreaterThan", "1", true)]
    [DataRow("IsGreaterThanOrEqualTo", "2", true)]
    [DataRow("IsLessThan", "2", false)]
    [DataRow("IsLessThanOrEqualTo", "2", true)]
    [DataRow("IsGreaterThan", "2147483647", false)]
    [DataRow("IsLessThanOrEqualTo", "2147483647", true)]
    public void NumericRelationsCompareWithoutInclusiveEndpointOverflow(string operation, string value, bool expected)
    {
        var restriction = Parse(Comparison(operation, "folder:TotalCount", value));
        Assert.AreEqual(expected, restriction.Matches(Snapshot(Child, "child", Parent), childCount: 0));
    }

    [TestMethod]
    [DataRow("folder:TotalCount", "2", true)]
    [DataRow("folder:UnreadCount", "1", true)]
    [DataRow("folder:ChildFolderCount", "3", true)]
    [DataRow("folder:ChildFolderCount", "2", false)]
    public void NumericFieldsSelectTheirOwnSnapshotOrImmediateChildCount(string field, string value, bool expected)
    {
        Assert.AreEqual(expected, Parse(Comparison("IsEqualTo", field, value)).Matches(Snapshot(Child, "child", Parent), childCount: 3));
    }

    [TestMethod]
    public void DeepMatchingTraversesNonmatchingParentsWithoutEscapingTheRequestedScope()
    {
        var graph = Graph();
        var restriction = Parse(Contains("FullString", "Exact", "match"));
        Assert.AreSequenceEqual<Guid>([Descendant], graph.Find(Parent, deep: true, restriction));
        Assert.HasCount(0, graph.Find(Parent, deep: false, restriction));
        var negation = Parse("<t:Not>" + Contains("FullString", "Exact", "absent") + "</t:Not>");
        Assert.AreSequenceEqual<Guid>([Child, Descendant], graph.Find(Parent, deep: true, negation));
        Assert.DoesNotContain(Outside, graph.Find(Parent, deep: true, negation));
    }

    [TestMethod]
    public void BooleanCompositionIsAppliedBeforePagingCountsAndUnpagedOverflow()
    {
        var graph = Graph();
        var restriction = Parse("<t:And><t:Or>" + Contains("FullString", "Exact", "match")
            + Contains("FullString", "Exact", "does-not-match") + "</t:Or><t:Not>"
            + Comparison("IsEqualTo", "folder:ChildFolderCount", "1") + "</t:Not></t:And>");
        var request = new GatewayEwsRequest("FindFolder", new HashSet<string>(StringComparer.Ordinal) { "FolderId", "DisplayName" },
            [new(GatewayEwsFolderIdCodec.Encode(Account, Parent), Distinguished: false, Mailbox: null)],
            Deep: true, Offset: 0, Limit: 1, Indexed: false, FolderRestriction: restriction);
        var response = XDocument.Parse(GatewayEwsFolderResponse.Render(request, graph, "owner@example.test"));
        Assert.AreEqual("NoError", response.Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.AreEqual("1", (string?)response.Descendants(GatewayEwsSoap.Messages + "RootFolder").Single().Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.AreEqual("match", response.Descendants(GatewayEwsSoap.Types + "DisplayName").Single().Value, StringComparer.Ordinal);
        var beyond = XDocument.Parse(GatewayEwsFolderResponse.Render(request with { Indexed = true, Offset = int.MaxValue }, graph, "owner@example.test"));
        Assert.AreEqual("1", (string?)beyond.Descendants(GatewayEwsSoap.Messages + "RootFolder").Single().Attribute("IndexedPagingOffset"), StringComparer.Ordinal);
        Assert.IsEmpty(beyond.Descendants(GatewayEwsSoap.Types + "Folder"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestrictionFollowsOptionalViewAndPrecedesParentReferences(bool paged)
    {
        var page = paged ? "<m:IndexedPageFolderView Offset='0' BasePoint='Beginning' MaxEntriesReturned='1'/>" : "";
        var body = Body(page + Restriction(Contains("Substring", "Exact", "x")));
        await using var bodyLifetime = body.ConfigureAwait(false);
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(request.FolderRestriction);
        Assert.AreEqual(paged, request.Indexed);
        Assert.IsFalse(request.IsMutation);
    }

    [TestMethod]
    [DataRow("<t:And/>", "ErrorSchemaValidation")]
    [DataRow("<t:Or><t:Not/></t:Or>", "ErrorSchemaValidation")]
    [DataRow("<t:Exists><t:FieldURI FieldURI='folder:DisplayName'/></t:Exists>", "ErrorInvalidRequest")]
    [DataRow("<t:Contains ContainmentMode='PrefixOnWords' ContainmentComparison='Exact'><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value='x'/></t:Contains>", "ErrorInvalidRequest")]
    [DataRow("<t:Contains ContainmentMode='FullString' ContainmentComparison='IgnoreNonSpacingCharacters'><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value='x'/></t:Contains>", "ErrorInvalidRequest")]
    [DataRow("<t:Contains><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value='x'/></t:Contains>", "ErrorInvalidRequest")]
    [DataRow("<t:Contains ContainmentMode='FullString' ContainmentComparison='Exact'><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value=''/></t:Contains>", "ErrorInvalidRequest")]
    [DataRow("<t:Contains ContainmentMode='FullString' ContainmentComparison='Exact'><t:FieldURI FieldURI='item:Subject'/><t:Constant Value='x'/></t:Contains>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:IsEqualTo><t:FieldURI FieldURI='folder:TotalCount'/><t:FieldURIOrConstant><t:Constant Value='-1'/></t:FieldURIOrConstant></t:IsEqualTo>", "ErrorSchemaValidation")]
    [DataRow("<t:IsEqualTo><t:FieldURI FieldURI='folder:TotalCount'/><t:FieldURIOrConstant><t:Constant Value='2147483648'/></t:FieldURIOrConstant></t:IsEqualTo>", "ErrorSchemaValidation")]
    [DataRow("<t:IsEqualTo><t:FieldURI FieldURI='folder:TotalCount'/><t:FieldURIOrConstant><t:FieldURI FieldURI='folder:UnreadCount'/></t:FieldURIOrConstant></t:IsEqualTo>", "ErrorInvalidRequest")]
    [DataRow("<t:IsEqualTo><t:FieldURI FieldURI='folder:DisplayName'/><t:FieldURIOrConstant><t:Constant Value='0'/></t:FieldURIOrConstant></t:IsEqualTo>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:Not xmlns:t='urn:foreign'/>", "ErrorSchemaValidation")]
    public async Task UnsupportedOrMalformedPredicatesAreRefusedBeforeDispatch(string expression, string expected)
    {
        var body = Body(Restriction(expression));
        await using var bodyLifetime = body.ConfigureAwait(false);
        var error = await Assert.ThrowsExactlyAsync<GatewayEwsRequestException>(() =>
            GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(expected, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(8, true)]
    [DataRow(9, false)]
    public async Task DepthCountsExpressionEdges(int depth, bool admitted)
    {
        var expression = string.Concat(Enumerable.Repeat("<t:Not>", depth))
            + Comparison("IsEqualTo", "folder:TotalCount", "0") + string.Concat(Enumerable.Repeat("</t:Not>", depth));
        var body = Body(Restriction(expression));
        await using var bodyLifetime = body.ConfigureAwait(false);
        if (admitted)
        {
            Assert.IsNotNull((await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).FolderRestriction);
        }
        else
        {
            Assert.AreEqual("ErrorInvalidRequest", (await Assert.ThrowsExactlyAsync<GatewayEwsRequestException>(() =>
                GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        }
    }

    [TestMethod]
    [DataRow(31, true)]
    [DataRow(32, false)]
    public async Task NodeBudgetCountsTheCompositeAndEveryTerm(int leaves, bool admitted)
    {
        var expression = "<t:Or>" + string.Concat(Enumerable.Repeat(Comparison("IsEqualTo", "folder:TotalCount", "0"), leaves)) + "</t:Or>";
        var body = Body(Restriction(expression));
        await using var bodyLifetime = body.ConfigureAwait(false);
        if (admitted)
        {
            Assert.IsNotNull((await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).FolderRestriction);
        }
        else
        {
            Assert.AreEqual("ErrorInvalidRequest", (await Assert.ThrowsExactlyAsync<GatewayEwsRequestException>(() =>
                GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        }
    }

    [TestMethod]
    [DataRow(127, 1, true)]
    [DataRow(128, 0, false)]
    public async Task NameConstantBudgetCountsDecodedUtf8Bytes(int accents, int ascii, bool admitted)
    {
        var value = new string('é', accents) + new string('a', ascii);
        var body = Body(Restriction(Contains("Substring", "Exact", value)));
        await using var bodyLifetime = body.ConfigureAwait(false);
        if (admitted)
        {
            Assert.IsNotNull((await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).FolderRestriction);
        }
        else
        {
            Assert.AreEqual("ErrorInvalidRequest", (await Assert.ThrowsExactlyAsync<GatewayEwsRequestException>(() =>
                GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        }
    }

    [TestMethod]
    public async Task RepeatedReorderedAndNonregisteredRequestFieldsDoNotAcquireNewSemantics()
    {
        var restriction = Restriction(Contains("FullString", "Exact", "x"));
        foreach (var fields in new[] { restriction + restriction,
            restriction + "<m:IndexedPageFolderView Offset='0' BasePoint='Beginning'/>", "<m:SortOrder/>", })
        {
            var body = Body(fields);
            await using var bodyLifetime = body.ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<GatewayEwsRequestException>(() =>
                GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private static MemoryStream Body(string fields) => new(Encoding.UTF8.GetBytes(GatewayEwsTests.Request(
        "FindFolder", "<t:DistinguishedFolderId Id='inbox'/>", page: fields)));
    private static string Restriction(string expression) => "<m:Restriction>" + expression + "</m:Restriction>";
    private static string Contains(string mode, string comparison, string value) =>
        $"<t:Contains ContainmentMode='{mode}' ContainmentComparison='{comparison}'><t:FieldURI FieldURI='folder:DisplayName'/><t:Constant Value='{value}'/></t:Contains>";
    private static string Comparison(string operation, string field, string value) =>
        $"<t:{operation}><t:FieldURI FieldURI='{field}'/><t:FieldURIOrConstant><t:Constant Value='{value}'/></t:FieldURIOrConstant></t:{operation}>";
    private static GatewayEwsFolderRestriction Parse(string expression) => GatewayEwsFolderRestriction.Parse(XElement.Parse(
        $"<m:Restriction xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'>{expression}</m:Restriction>"));
    private static MailFolderSnapshot Snapshot(Guid id, string name, Guid? parent) => new(id, name, parent,
        Role: null, SortOrder: 0, IsSubscribed: true, TotalEmails: 2, UnreadEmails: 1,
        TotalThreads: 2, UnreadThreads: 1, IsProtected: false);
    private static GatewayEwsFolderGraph Graph() => new(Account, "state",
        [Snapshot(Parent, "parent", parent: null), Snapshot(Child, "does-not-match", Parent),
         Snapshot(Descendant, "match", Child), Snapshot(Outside, "match", parent: null)]);
}
