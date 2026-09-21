using System.Net;
using System.Xml.Linq;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DavProtocolTests
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";

    [TestMethod]
    public async Task DiscoveryAuthenticatesAndPublishesPrincipalAndDefaultHomes()
    {
        await using var fixture = await DavFixture.CreateAsync();

        using var options = await fixture.SendAsync(
            "OPTIONS",
            "/dav/",
            authenticate: false);
        Assert.AreEqual(HttpStatusCode.OK, options.StatusCode);
        StringAssert.Contains(options.Headers.GetValues("DAV").Single(), "calendar-access");
        StringAssert.Contains(options.Headers.GetValues("DAV").Single(), "addressbook");
        StringAssert.Contains(options.Content.Headers.Allow.ToString(), "REPORT");

        using var redirect = await fixture.SendAsync(
            "PROPFIND",
            "/.well-known/caldav",
            headers: Header("Depth", "0"),
            authenticate: false);
        Assert.AreEqual(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.AreEqual("/dav/", redirect.Headers.Location?.OriginalString);

        using var unauthorized = await fixture.SendAsync(
            "PROPFIND",
            "/dav/",
            headers: Header("Depth", "0"),
            authenticate: false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        StringAssert.Contains(
            unauthorized.Headers.WwwAuthenticate.Single().ToString(),
            "mk8.email DAV");

        using var discovery = await fixture.SendAsync(
            "PROPFIND",
            "/dav/",
            Propfind(
                "<D:current-user-principal/><C:calendar-home-set/>" +
                "<A:addressbook-home-set/><D:resourcetype/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, discovery.StatusCode);
        var discoveryXml = await ReadXmlAsync(discovery);
        var hrefs = Hrefs(discoveryXml);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "/dav/",
                fixture.PrincipalPath,
                fixture.CalendarHomePath,
                fixture.AddressBookHomePath,
            },
            hrefs);

        using var calendars = await fixture.SendAsync(
            "PROPFIND",
            fixture.CalendarHomePath,
            Propfind("<D:displayname/><D:resourcetype/><D:sync-token/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, calendars.StatusCode);
        var calendarXml = await ReadXmlAsync(calendars);
        Assert.IsTrue(Hrefs(calendarXml).Contains(fixture.CalendarHomePath + "default/"));
        Assert.IsNotNull(calendarXml.Descendants(CalDav + "calendar").SingleOrDefault());

        using var addressBooks = await fixture.SendAsync(
            "PROPFIND",
            fixture.AddressBookHomePath,
            Propfind("<D:displayname/><D:resourcetype/><D:sync-token/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, addressBooks.StatusCode);
        var addressBookXml = await ReadXmlAsync(addressBooks);
        Assert.IsTrue(Hrefs(addressBookXml).Contains(fixture.AddressBookHomePath + "default/"));
        Assert.IsNotNull(addressBookXml.Descendants(CardDav + "addressbook").SingleOrDefault());
    }

    [TestMethod]
    public async Task CalDavSupportsCollectionResourceQueryAndIncrementalSyncLifecycle()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var collection = fixture.CalendarHomePath + "team/";
        var resource = collection + "planning.ics";

        const string createCollection = """
        <?xml version="1.0" encoding="utf-8"?>
        <C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:I="http://apple.com/ns/ical/">
          <D:set><D:prop>
            <D:displayname>Team planning</D:displayname>
            <C:calendar-description>Shared release plan</C:calendar-description>
            <I:calendar-color>#3366ffff</I:calendar-color>
            <C:supported-calendar-component-set><C:comp name="VEVENT"/><C:comp name="VTODO"/></C:supported-calendar-component-set>
          </D:prop></D:set>
        </C:mkcalendar>
        """;
        using var createdCollection = await fixture.SendAsync(
            "MKCALENDAR",
            collection,
            createCollection);
        Assert.AreEqual(HttpStatusCode.Created, createdCollection.StatusCode);

        const string calendar = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//DAV tests//EN
        METHOD:REQUEST
        BEGIN:VEVENT
        UID:release-planning-1@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART:20261001T100000Z
        DTEND:20261001T110000Z
        RRULE:FREQ=WEEKLY;COUNT=4
        ORGANIZER:mailto:dav.user@mk8n.com
        ATTENDEE;CN=Engineering;PARTSTAT=NEEDS-ACTION:mailto:engineering@mk8n.com
        SUMMARY:Release planning
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT
        END:VCALENDAR
        """;
        using var created = await fixture.SendAsync(
            "PUT",
            resource,
            calendar,
            "text/calendar; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var firstEtag = created.Headers.ETag?.Tag;
        Assert.IsNotNull(firstEtag);

        using var duplicateCreate = await fixture.SendAsync(
            "PUT",
            resource,
            calendar,
            "text/calendar; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, duplicateCreate.StatusCode);

        using var get = await fixture.SendAsync("GET", resource);
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.AreEqual(calendar, await get.Content.ReadAsStringAsync());
        Assert.AreEqual(firstEtag, get.Headers.ETag?.Tag);

        const string query = """
        <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:prop><D:getetag/><C:calendar-data/></D:prop>
          <C:filter><C:comp-filter name="VCALENDAR"><C:comp-filter name="VEVENT"/></C:comp-filter></C:filter>
        </C:calendar-query>
        """;
        using var queried = await fixture.SendAsync(
            "REPORT",
            collection,
            query,
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, queried.StatusCode);
        var queryXml = await ReadXmlAsync(queried);
        Assert.IsTrue(Hrefs(queryXml).Contains(resource));
        StringAssert.Contains(queryXml.Descendants(CalDav + "calendar-data").Single().Value, "RRULE:FREQ=WEEKLY");

        const string multiget = """
        <C:calendar-multiget xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:prop><D:getetag/><C:calendar-data/></D:prop>
          <D:href>{0}</D:href>
        </C:calendar-multiget>
        """;
        using var multi = await fixture.SendAsync(
            "REPORT",
            collection,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, multiget, resource));
        Assert.AreEqual((HttpStatusCode)207, multi.StatusCode);
        Assert.IsTrue(Hrefs(await ReadXmlAsync(multi)).Contains(resource));

        using var initialSync = await fixture.SendAsync(
            "REPORT",
            collection,
            SyncReport(string.Empty));
        Assert.AreEqual((HttpStatusCode)207, initialSync.StatusCode);
        var initialSyncXml = await ReadXmlAsync(initialSync);
        var initialToken = initialSyncXml.Root!.Element(Dav + "sync-token")!.Value;
        Assert.IsTrue(Hrefs(initialSyncXml).Contains(resource));

        var updatedCalendar = calendar.Replace(
            "SUMMARY:Release planning",
            "SUMMARY:Release planning updated",
            StringComparison.Ordinal);
        using var failedUpdate = await fixture.SendAsync(
            "PUT",
            resource,
            updatedCalendar,
            "text/calendar; charset=utf-8",
            Header("If-Match", "\"not-the-current-etag\""));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, failedUpdate.StatusCode);

        using var updated = await fixture.SendAsync(
            "PUT",
            resource,
            updatedCalendar,
            "text/calendar; charset=utf-8",
            Header("If-Match", firstEtag));
        Assert.AreEqual(HttpStatusCode.NoContent, updated.StatusCode);
        var secondEtag = updated.Headers.ETag?.Tag;
        Assert.IsNotNull(secondEtag);
        Assert.AreNotEqual(firstEtag, secondEtag);

        using var deleted = await fixture.SendAsync(
            "DELETE",
            resource,
            headers: Header("If-Match", secondEtag));
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        using var delta = await fixture.SendAsync(
            "REPORT",
            collection,
            SyncReport(initialToken));
        Assert.AreEqual((HttpStatusCode)207, delta.StatusCode);
        var deltaXml = await ReadXmlAsync(delta);
        var deletedResponse = deltaXml.Descendants(Dav + "response")
            .Single(element => element.Element(Dav + "href")?.Value == resource);
        StringAssert.Contains(deletedResponse.Element(Dav + "status")!.Value, "404");
        Assert.AreNotEqual(
            initialToken,
            deltaXml.Root!.Element(Dav + "sync-token")!.Value);
    }

    [TestMethod]
    public async Task CardDavSupportsContactsGroupsFiltersMetadataAndTenantIsolation()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var collection = fixture.AddressBookHomePath + "directory/";
        var alicePath = collection + "alice.vcf";
        var groupPath = collection + "engineering.vcf";

        const string extendedMkCol = """
        <D:mkcol xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
          <D:set><D:prop>
            <D:resourcetype><D:collection/><A:addressbook/></D:resourcetype>
            <D:displayname>Company directory</D:displayname>
            <A:addressbook-description>Synced contacts</A:addressbook-description>
          </D:prop></D:set>
        </D:mkcol>
        """;
        using var createdCollection = await fixture.SendAsync("MKCOL", collection, extendedMkCol);
        Assert.AreEqual(HttpStatusCode.Created, createdCollection.StatusCode);

        const string alice = """
        BEGIN:VCARD
        VERSION:4.0
        UID:alice@example.net
        KIND:individual
        FN:Alice Example
        N:Example;Alice;;;
        EMAIL;TYPE=work:alice@example.net
        TEL;TYPE=work:+38515550100
        END:VCARD
        """;
        using var createdAlice = await fixture.SendAsync(
            "PUT",
            alicePath,
            alice,
            "text/vcard; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, createdAlice.StatusCode);

        const string group = """
        BEGIN:VCARD
        VERSION:4.0
        UID:engineering@example.net
        KIND:group
        FN:Engineering
        MEMBER:urn:uuid:alice@example.net
        END:VCARD
        """;
        using var createdGroup = await fixture.SendAsync(
            "PUT",
            groupPath,
            group,
            "text/vcard; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, createdGroup.StatusCode);

        const string query = """
        <A:addressbook-query xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
          <D:prop><D:getetag/><A:address-data/></D:prop>
          <A:filter test="anyof"><A:prop-filter name="EMAIL"><A:text-match match-type="contains">example.net</A:text-match></A:prop-filter></A:filter>
        </A:addressbook-query>
        """;
        using var queryResponse = await fixture.SendAsync("REPORT", collection, query);
        Assert.AreEqual((HttpStatusCode)207, queryResponse.StatusCode);
        var queryXml = await ReadXmlAsync(queryResponse);
        CollectionAssert.AreEqual(new[] { alicePath }, Hrefs(queryXml));
        StringAssert.Contains(queryXml.Descendants(CardDav + "address-data").Single().Value, "FN:Alice Example");

        const string duplicateUid = """
        BEGIN:VCARD
        VERSION:3.0
        UID:alice@example.net
        FN:Alice Duplicate
        END:VCARD
        """;
        using var conflict = await fixture.SendAsync(
            "PUT",
            collection + "duplicate.vcf",
            duplicateUid,
            "text/x-vcard");
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.IsNotNull((await ReadXmlAsync(conflict)).Descendants(CardDav + "no-uid-conflict").SingleOrDefault());

        const string patch = """
        <D:propertyupdate xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
          <D:set><D:prop><D:displayname>Updated directory</D:displayname><A:addressbook-description>Managed contacts</A:addressbook-description></D:prop></D:set>
        </D:propertyupdate>
        """;
        using var patched = await fixture.SendAsync("PROPPATCH", collection, patch);
        Assert.AreEqual((HttpStatusCode)207, patched.StatusCode);

        using var properties = await fixture.SendAsync(
            "PROPFIND",
            collection,
            Propfind("<D:displayname/><A:addressbook-description/>"),
            headers: Header("Depth", "0"));
        var propertyXml = await ReadXmlAsync(properties);
        Assert.AreEqual("Updated directory", propertyXml.Descendants(Dav + "displayname").Single().Value);
        Assert.AreEqual("Managed contacts", propertyXml.Descendants(CardDav + "addressbook-description").Single().Value);

        var foreignPath = $"/dav/addressbooks/{Guid.CreateVersion7():N}/default/";
        using var foreign = await fixture.SendAsync(
            "PROPFIND",
            foreignPath,
            Propfind("<D:displayname/>"),
            headers: Header("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static IReadOnlyDictionary<string, string> Header(string name, string? value)
    {
        Assert.IsNotNull(value);
        return new Dictionary<string, string> { [name] = value };
    }

    private static string Propfind(string properties) => $$"""
        <D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:A="urn:ietf:params:xml:ns:carddav">
          <D:prop>{{properties}}</D:prop>
        </D:propfind>
        """;

    private static string SyncReport(string token) => $$"""
        <D:sync-collection xmlns:D="DAV:">
          <D:sync-token>{{token}}</D:sync-token>
          <D:sync-level>1</D:sync-level>
          <D:prop><D:getetag/></D:prop>
        </D:sync-collection>
        """;

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response) =>
        XDocument.Parse(await response.Content.ReadAsStringAsync());

    private static string[] Hrefs(XDocument document) => document
        .Descendants(Dav + "response")
        .Select(response => response.Element(Dav + "href")?.Value)
        .Where(value => value is not null)
        .Cast<string>()
        .ToArray();
}
