using System.Net;
using System.Text.Json;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HttpLifecycleScopesActualDomainSdkAndExcludesJournalOrRefusedRequests(bool refused)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        Assert.AreEqual(TimeSpan.FromSeconds(15), fixture.Client.Timeout);
        var xml = refused
            ? DeleteRequest("<t:ItemId Id='unsupported'/>", "DeleteType='HardDelete' SuppressReadReceipts='true' AffectedTaskOccurrences='AllOccurrences'")
            : Request("GetFolder", "<t:DistinguishedFolderId Id='inbox'/>");
        using var content = XmlContent(xml);
        using var response = await fixture.Client.PostAsync(new Uri("/EWS/Exchange.asmx", UriKind.Relative), content).ConfigureAwait(false);
        Assert.AreEqual(refused ? HttpStatusCode.InternalServerError : HttpStatusCode.OK, response.StatusCode);
        using var database = JsonDocument.Parse("{}");
        using var report = JsonDocument.Parse(fixture.Diagnostics.Report(database.RootElement, "Running"));
        var events = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var http = events.Where(value => value.GetProperty("Phase").GetString()?.StartsWith("Http", StringComparison.Ordinal) == true).ToArray();
        if (refused)
        {
            Assert.HasCount(0, http);
            Assert.IsFalse(events.Any(value => value.GetProperty("Phase").GetString() is "DispatchStart"));
        }
        else
        {
            Assert.IsTrue(http.Any(value => value.GetProperty("Phase").GetString() is "HttpRequestStart"));
            Assert.IsTrue(http.Any(value => value.GetProperty("Phase").GetString() is "HttpResponseHeadersStop"));
            foreach (var value in http)
                Assert.IsTrue(events.Any(start => start.GetProperty("Phase").GetString() is "AzureStart"
                    && start.GetProperty("Span").GetGuid() == value.GetProperty("Span").GetGuid()
                    && start.GetProperty("Attempt").GetInt32() == value.GetProperty("Attempt").GetInt32()
                    && start.GetProperty("Request").GetGuid() == value.GetProperty("Request").GetGuid()));
        }
        Assert.IsTrue(events.Any(value => value.GetProperty("Phase").GetString() is "JournalInboundComplete"));
        Assert.IsTrue(events.Any(value => value.GetProperty("Phase").GetString() is "JournalOutboundComplete"));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("Exchange.asmx", StringComparison.Ordinal));
        Assert.IsFalse(report.RootElement.GetRawText().Contains("owner@example.test", StringComparison.Ordinal));
    }
}
