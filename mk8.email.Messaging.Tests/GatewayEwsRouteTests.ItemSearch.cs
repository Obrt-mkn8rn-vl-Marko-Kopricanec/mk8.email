using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] SearchShapeProperties = ["item:Size", "item:DateTimeReceived", "item:IsDraft", "message:IsRead"];

    [TestMethod]
    [DataRow("read", "0,2")]
    [DataRow("draft", "2,3")]
    [DataRow("not-read", "0,2")]
    [DataRow("and", "0")]
    [DataRow("or", "1,2,3")]
    [DataRow("not", "0")]
    [DataRow("size-equal", "1,2")]
    [DataRow("size-not-equal", "0,3")]
    [DataRow("size-greater", "3")]
    [DataRow("size-at-least", "1,2,3")]
    [DataRow("size-less", "0")]
    [DataRow("size-at-most", "0,1,2")]
    [DataRow("received-after", "2,3")]
    [DataRow("received-before", "0,1")]
    [DataRow("size-zero", "")]
    public async Task ItemSearchRealWorkerMatchesRestrictedMetadataWithoutBodyOrForeignScope(string scenario, string expectedIndices)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rows = await SeedSearchItemsAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        var seen = SearchComparison("message:IsRead", "IsEqualTo", "true");
        var draft = SearchComparison("item:IsDraft", "IsEqualTo", "true");
        var size = rows[1].SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expression = scenario switch
        {
            "read" => SearchComparison("message:IsRead", "IsEqualTo", "false"),
            "draft" => draft,
            "not-read" => SearchComparison("message:IsRead", "IsNotEqualTo", "1"),
            "and" => "<t:And><t:Not>" + seen + "</t:Not><t:Not>" + draft + "</t:Not></t:And>",
            "or" => "<t:Or>" + seen + draft + "</t:Or>",
            "not" => "<t:Not><t:Or>" + seen + draft + "</t:Or></t:Not>",
            "size-equal" => SearchComparison("item:Size", "IsEqualTo", size),
            "size-not-equal" => SearchComparison("item:Size", "IsNotEqualTo", size),
            "size-greater" => SearchComparison("item:Size", "IsGreaterThan", size),
            "size-at-least" => SearchComparison("item:Size", "IsGreaterThanOrEqualTo", size),
            "size-less" => SearchComparison("item:Size", "IsLessThan", size),
            "size-at-most" => SearchComparison("item:Size", "IsLessThanOrEqualTo", size),
            "received-after" => SearchComparison("item:DateTimeReceived", "IsGreaterThanOrEqualTo", "2026-10-08T12:02:00Z"),
            "received-before" => SearchComparison("item:DateTimeReceived", "IsLessThan", "2026-10-08T14:02:00+02:00"),
            _ => SearchComparison("item:Size", "IsEqualTo", "0"),
        };
        var result = await SearchPageAsync(fixture, "<m:Restriction>" + expression + "</m:Restriction>" + SearchOrder("item:DateTimeReceived", true)).ConfigureAwait(false);
        var expected = expectedIndices.Length == 0 ? [] : expectedIndices.Split(',').Select(index => rows[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)].Id).ToArray();
        AssertFindPage(result, expected, expected.Length, expected.Length, true);
        var messages = result.Descendants(Types + "Message").ToArray();
        for (var index = 0; index < expected.Length; index++)
        {
            var row = rows.Single(item => item.Id == expected[index]);
            Assert.AreEqual(row.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), messages[index].Element(Types + "Size")!.Value, StringComparer.Ordinal);
            Assert.AreEqual(row.IsRead ? "true" : "false", messages[index].Element(Types + "IsRead")!.Value, StringComparer.Ordinal);
        }
        await fixture.AssertWorkerOperationsAsync(Enumerable.Repeat(ApplicationOperations.MailOperationExecute, expected.Length == 0 ? 5 : 6)
            .Prepend(ApplicationOperations.JmapProfileGet).ToArray()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("item:DateTimeReceived", true, "0,1,2,3")]
    [DataRow("item:DateTimeReceived", false, "3,2,1,0")]
    [DataRow("item:Size", true, "0,2,1,3")]
    [DataRow("item:Size", false, "3,2,1,0")]
    public async Task ItemSearchSortPagingRetainsComparatorOrderAndFilteredTotals(string field, bool ascending, string order)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rows = await SeedSearchItemsAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        // Reverse-time secondary comparator makes equal-size order observable.
        var sort = SearchOrder(field, ascending);
        if (field is "item:Size") sort = sort.Replace("</m:SortOrder>", "<t:FieldOrder Order='Descending'><t:FieldURI FieldURI='item:DateTimeReceived'/></t:FieldOrder></m:SortOrder>", StringComparison.Ordinal);
        var ids = order.Split(',').Select(index => rows[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)].Id).ToArray();
        AssertFindPage(await SearchPageAsync(fixture, sort, limit: 2).ConfigureAwait(false), ids[..2], 4, 2, false);
        AssertFindPage(await SearchPageAsync(fixture, sort, offset: 2, limit: 2).ConfigureAwait(false), ids[2..], 4, 4, true);
        AssertFindPage(await SearchPageAsync(fixture, sort, offset: int.MaxValue, limit: 2).ConfigureAwait(false), [], 4, 4, true);
    }

    [TestMethod]
    [DataRow("<m:Restriction><t:Contains/></m:Restriction>", "ErrorInvalidRequest")]
    [DataRow("<m:Restriction><t:IsEqualTo><t:FieldURI FieldURI='item:HasAttachments'/><t:FieldURIOrConstant><t:Constant Value='true'/></t:FieldURIOrConstant></t:IsEqualTo></m:Restriction>", "ErrorInvalidPropertyRequest")]
    [DataRow("<m:Restriction><t:IsEqualTo><t:FieldURI FieldURI='message:IsRead'/><t:FieldURIOrConstant><t:Constant Value='True'/></t:FieldURIOrConstant></t:IsEqualTo></m:Restriction>", "ErrorSchemaValidation")]
    [DataRow("<m:SortOrder><t:FieldOrder Order='Ascending'><t:FieldURI FieldURI='item:Subject'/></t:FieldOrder></m:SortOrder>", "ErrorInvalidPropertyRequest")]
    [DataRow("<m:Restriction><?unsupported x?><t:Not/></m:Restriction>", "ErrorSchemaValidation")]
    public async Task ItemSearchInvalidOrUnsupportedInputHasJournaledSoapAndNoWorkerDispatch(string fields, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(SearchRequest(fields, "<t:DistinguishedFolderId Id='inbox'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("read")]
    [DataRow("scope")]
    public async Task ItemSearchCommittedRaceWithholdsFilteredMixedState(string change)
    {
        CaptureFixture? fixture = null;
        Guid item = Guid.Empty;
        var injections = 0;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner, MailOperationKind.FindMessages, async token =>
        {
            if (Interlocked.CompareExchange(ref injections, 1, 0) != 0) return;
            var database = Context(fixture!);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var email = await database.Emails.SingleAsync(row => row.Id == item, token).ConfigureAwait(false);
            if (change is "read") email.IsRead = true;
            else email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
            await database.SaveChangesAsync(token).ConfigureAwait(false);
        })).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        item = (await SeedSearchItemsAsync(fixture).ConfigureAwait(false))[0].Id;
        Authenticate(fixture);
        using var content = XmlContent(SearchRequest("<m:Restriction>" + SearchComparison("message:IsRead", "IsEqualTo", "false") + "</m:Restriction>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(1, injections);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("RootFolder", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 503, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task ItemSearchJournalFailureWithholdsFilteredMetadata(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        _ = await SeedSearchItemsAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(SearchRequest("<m:Restriction>" + SearchComparison("message:IsRead", "IsEqualTo", "false") + "</m:Restriction>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsTrue((int)response.StatusCode >= 500);
        Assert.IsFalse(body.Contains("ItemId", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("NoError", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ItemSearchRepeatedAndForeignParentsKeepOrderedIndependentAuthority()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var rows = await SeedSearchItemsAsync(fixture).ConfigureAwait(false);
        Authenticate(fixture);
        var owned = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>";
        var refs = $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)}'/>"
            + owned + "<t:DistinguishedFolderId Id='msgfolderroot'/>" + owned;
        using var content = XmlContent(SearchRequest("<m:Restriction><t:Not>" + SearchComparison("message:IsRead", "IsEqualTo", "true")
            + "</t:Not></m:Restriction>" + SearchOrder("item:DateTimeReceived", true), refs));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = XDocument.Parse(body);
        var results = document.Descendants(Messages + "FindItemResponseMessage").ToArray();
        Assert.HasCount(4, results);
        Assert.AreEqual("ErrorAccessDenied", results[0].Element(Messages + "ResponseCode")!.Value, StringComparer.Ordinal);
        Assert.AreEqual(results[1].ToString(), results[3].ToString(), StringComparer.Ordinal);
        Assert.IsEmpty(results[2].Descendants(Types + "Message"));
        CollectionAssert.AreEqual(new string[] { ItemId(rows[0].Id), ItemId(rows[2].Id) }, results[1].Descendants(Types + "ItemId").Select(item => (string)item.Attribute("Id")!).ToArray());
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("wrong")]
    [DataRow("plaintext")]
    public async Task ItemSearchDoesNotAcquireAuthorityFromAValidExpression(string mode)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        if (mode is "missing") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "wrong") fixture.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("owner@example.test:wrong")));
        if (mode is "plaintext")
        {
            fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
            fixture.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        }
        using var content = XmlContent(SearchRequest("<m:Restriction>" + SearchComparison("message:IsRead", "IsEqualTo", "false") + "</m:Restriction>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = mode is "plaintext" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        Assert.AreEqual(status, response.StatusCode);
        AssertFault(body, "ErrorAccessDenied");
        if (mode is "wrong") await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        else await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, (int)status, body, rejection: false).ConfigureAwait(false);
    }

    private static async Task<EmailDB[]> SeedSearchItemsAsync(CaptureFixture fixture)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var rows = Enumerable.Range(0, 7).Select(index => new EmailDB
        {
            Id = Guid.CreateVersion7(),
            FolderId = index == 4 ? GatewayEwsFixtureDomain.ForeignFolderId : index == 5 ? GatewayEwsFixtureDomain.GrandchildId : GatewayEwsFixtureDomain.ChildId,
            Uid = index + 300,
            IsRead = index is 1 or 3,
            IsDraft = index is 2 or 3,
            IsDeleted = index == 6,
            ReceivedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
        }).ToArray();
        await database.Emails.AddRangeAsync(rows).ConfigureAwait(false);
        for (var index = 0; index < rows.Length; index++)
        {
            var padding = index is 1 or 2 ? 20 : index == 3 ? 40 : 10;
            await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().SetAsync(rows[index],
                Encoding.UTF8.GetBytes(ItemMime + new string('x', padding)), CancellationToken.None).ConfigureAwait(false);
        }
        await database.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return rows[..4];
    }

    private static async Task<XDocument> SearchPageAsync(CaptureFixture fixture, string fields, int offset = 0, int limit = 32)
    {
        using var content = XmlContent(SearchRequest(fields, offset: offset, limit: limit));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.IsFalse(body.Contains("PRIVATE BODY", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(body).Descendants(Types + "Body"));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(body);
    }
    private static string SearchComparison(string field, string comparison, string value) => $"<t:{comparison}><t:FieldURI FieldURI='{field}'/><t:FieldURIOrConstant><t:Constant Value='{value}'/></t:FieldURIOrConstant></t:{comparison}>";
    private static string SearchOrder(string field, bool ascending) => $"<m:SortOrder><t:FieldOrder Order='{(ascending ? "Ascending" : "Descending")}'><t:FieldURI FieldURI='{field}'/></t:FieldOrder></m:SortOrder>";
    private static string SearchRequest(string fields, string? refs = null, int offset = 0, int limit = 32)
    {
        var shape = "<t:AdditionalProperties>" + string.Concat(SearchShapeProperties.Select(field => $"<t:FieldURI FieldURI='{field}'/>")) + "</t:AdditionalProperties>";
        return FindRequest(refs ?? $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>",
            $"<m:IndexedPageItemView MaxEntriesReturned='{limit}' Offset='{offset}' BasePoint='Beginning'/>" + fields, shape);
    }
}
