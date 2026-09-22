using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

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
        StringAssert.Contains(options.Headers.GetValues("DAV").Single(), "calendar-schedule");
        StringAssert.Contains(options.Headers.GetValues("DAV").Single(), "addressbook");
        StringAssert.Contains(options.Headers.GetValues("DAV").Single(), "access-control");
        StringAssert.Contains(options.Content.Headers.Allow.ToString(), "REPORT");
        StringAssert.Contains(options.Content.Headers.Allow.ToString(), "ACL");

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

        using var scheduling = await fixture.SendAsync(
            "PROPFIND",
            fixture.PrincipalPath,
            Propfind("<C:schedule-inbox-URL/><C:schedule-outbox-URL/><C:calendar-user-address-set/>"),
            headers: Header("Depth", "0"));
        Assert.AreEqual((HttpStatusCode)207, scheduling.StatusCode);
        var schedulingXml = await ReadXmlAsync(scheduling);
        Assert.AreEqual(
            fixture.SchedulingInboxPath,
            schedulingXml.Descendants(CalDav + "schedule-inbox-URL")
                .Single().Element(Dav + "href")?.Value);
        Assert.AreEqual(
            fixture.SchedulingOutboxPath,
            schedulingXml.Descendants(CalDav + "schedule-outbox-URL")
                .Single().Element(Dav + "href")?.Value);
        Assert.AreEqual(
            $"mailto:{fixture.PrimaryAddress}",
            schedulingXml.Descendants(CalDav + "calendar-user-address-set")
                .Single().Element(Dav + "href")?.Value);
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
        string firstObjectName;
        using (var scope = fixture.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
                .DavResources.AsNoTracking()
                .SingleAsync(candidate => candidate.ResourceName == "planning.ics");
            Assert.IsNull(stored.Content);
            Assert.AreEqual("azure-blob", stored.ObjectProvider);
            firstObjectName = stored.ObjectName
                ?? throw new AssertFailedException("The DAV object name is missing.");
            Assert.IsTrue(scope.ServiceProvider.GetRequiredService<InMemoryLargeObjectStore>()
                .Contains(firstObjectName));
        }

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
        Assert.AreEqual(
            1,
            fixture.Services.GetRequiredService<InMemoryLargeObjectStore>().Count);

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
        using (var scope = fixture.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
                .DavResources.AsNoTracking()
                .SingleAsync(candidate => candidate.ResourceName == "planning.ics");
            Assert.IsNull(stored.Content);
            Assert.AreNotEqual(firstObjectName, stored.ObjectName);
            var objects = scope.ServiceProvider.GetRequiredService<InMemoryLargeObjectStore>();
            Assert.AreEqual(1, objects.Count);
            Assert.IsFalse(objects.Contains(firstObjectName));
            Assert.IsTrue(objects.Contains(stored.ObjectName!));
        }

        using var deleted = await fixture.SendAsync(
            "DELETE",
            resource,
            headers: Header("If-Match", secondEtag));
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(
            0,
            fixture.Services.GetRequiredService<InMemoryLargeObjectStore>().Count);

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
        UID;VALUE=text:alice@example.net
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
        UID;VALUE=text:engineering@example.net
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

    [TestMethod]
    public async Task CardDavHttpAndJmapHttpRoundTripRfc9555ContactData()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var collection = fixture.AddressBookHomePath + "jmap-roundtrip/";
        const string createAddressBook = """
            <D:mkcol xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop>
                <D:resourcetype><D:collection/><A:addressbook/></D:resourcetype>
                <D:displayname>JMAP round trip</D:displayname>
              </D:prop></D:set>
            </D:mkcol>
            """;
        using var createdAddressBook = await fixture.SendAsync(
            "MKCOL",
            collection,
            createAddressBook);
        Assert.AreEqual(HttpStatusCode.Created, createdAddressBook.StatusCode);

        const string vcard = """
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:carddav-http-roundtrip
            KIND:individual
            FN:Dr. John Philip Stevenson Jr.
            N:Stevenson;John;Philip,Paul;Dr.;Jr.,M.D.,A.C.P.;;Jr.
            ORG:ABC\, Inc.;North American Division;Marketing
            EMAIL;TYPE=work:john.stevenson@example.net
            END:VCARD
            """;
        var resource = collection + "john.vcf";
        using var created = await fixture.SendAsync(
            "PUT",
            resource,
            vcard,
            "text/vcard; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var query = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "ContactCard/query",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["filter"] = new JsonObject { ["uid"] = "carddav-http-roundtrip" },
                },
                "q1")),
        });
        var queryArguments = query["methodResponses"]!.AsArray()[0]!.AsArray()[1]!.AsObject();
        var cardId = queryArguments["ids"]!.AsArray().Single()!.GetValue<string>();

        var get = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "ContactCard/get",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["ids"] = new JsonArray(cardId),
                },
                "g1")),
        });
        var card = get["methodResponses"]!.AsArray()[0]!.AsArray()[1]!["list"]![0]!;
        Assert.AreEqual("Dr. John Philip Stevenson Jr.", card["name"]!["full"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[]
            {
                "surname", "given", "given2", "given2", "title", "credential",
                "credential", "generation",
            },
            card["name"]!["components"]!.AsArray()
                .Select(component => component!["kind"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "North American Division", "Marketing" },
            card["organizations"]!.AsObject().Single().Value!["units"]!.AsArray()
                .Select(unit => unit!["name"]!.GetValue<string>()).ToArray());

        var update = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "ContactCard/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["update"] = new JsonObject
                    {
                        [cardId] = new JsonObject { ["name/full"] = "Updated over JMAP" },
                    },
                },
                "s1")),
        });
        var updateArguments = update["methodResponses"]!.AsArray()[0]!.AsArray()[1]!;
        Assert.IsTrue(updateArguments["updated"]!.AsObject().ContainsKey(cardId));

        using var readBack = await fixture.SendAsync("GET", resource);
        Assert.AreEqual(HttpStatusCode.OK, readBack.StatusCode);
        StringAssert.Contains(await readBack.Content.ReadAsStringAsync(), "FN:Updated over JMAP");

        const string invalidMember = """
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:invalid-member
            KIND:group
            FN:Invalid Member
            MEMBER:relative-member
            END:VCARD
            """;
        using var rejected = await fixture.SendAsync(
            "PUT",
            collection + "invalid.vcf",
            invalidMember,
            "text/vcard; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
    }

    [TestMethod]
    public async Task CardDavEmbeddedJsContactCannotForgeUidOrLocalizedBlobAccess()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var collection = fixture.AddressBookHomePath + "embedded-guards/";
        const string createAddressBook = """
            <D:mkcol xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop>
                <D:resourcetype><D:collection/><A:addressbook/></D:resourcetype>
                <D:displayname>Embedded guards</D:displayname>
              </D:prop></D:set>
            </D:mkcol>
            """;
        using var createdAddressBook = await fixture.SendAsync("MKCOL", collection, createAddressBook);
        Assert.AreEqual(HttpStatusCode.Created, createdAddressBook.StatusCode);

        var duplicateEmbedded = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "forged-shared-uid",
            ["kind"] = "individual",
            ["name"] = new JsonObject { ["full"] = "Forged Shared" },
        };
        using var first = await fixture.SendAsync(
            "PUT",
            collection + "first.vcf",
            ForgedVCard("indexed-first-uid", "Indexed First", duplicateEmbedded),
            "text/vcard; charset=utf-8");
        using var second = await fixture.SendAsync(
            "PUT",
            collection + "second.vcf",
            ForgedVCard("indexed-second-uid", "Indexed Second", duplicateEmbedded),
            "text/vcard; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);

        var mismatchedProjection = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "indexed-semantic-uid",
            ["kind"] = "individual",
            ["name"] = new JsonObject { ["full"] = "Forged Semantic" },
        };
        using var semantic = await fixture.SendAsync(
            "PUT",
            collection + "semantic.vcf",
            ForgedVCard(
                "indexed-semantic-uid",
                "Indexed Semantic",
                mismatchedProjection,
                includeProdId: true),
            "text/vcard; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, semantic.StatusCode);

        var blobId = JmapId.UploadedBlob(Guid.CreateVersion7());
        var localizedBlob = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "indexed-localized-uid",
            ["kind"] = "individual",
            ["name"] = new JsonObject { ["full"] = "Indexed Localized" },
            ["localizations"] = new JsonObject
            {
                ["fr"] = new JsonObject
                {
                    ["media"] = new JsonObject
                    {
                        ["photo"] = new JsonObject
                        {
                            ["kind"] = "photo",
                            ["blobId"] = blobId,
                        },
                    },
                },
            },
        };
        using var localized = await fixture.SendAsync(
            "PUT",
            collection + "localized.vcf",
            ForgedVCard("indexed-localized-uid", "Indexed Localized", localizedBlob, includeProdId: true),
            "text/vcard; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, localized.StatusCode);

        var response = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(
                Query("forged", "forged-shared-uid"),
                Query("first", "indexed-first-uid"),
                Query("second", "indexed-second-uid"),
                Query("semantic", "indexed-semantic-uid"),
                Query("localized", "indexed-localized-uid")),
        });
        var methodResponses = response["methodResponses"]!.AsArray();
        Assert.AreEqual(0, methodResponses[0]![1]!["ids"]!.AsArray().Count);
        Assert.AreEqual(1, methodResponses[1]![1]!["ids"]!.AsArray().Count);
        Assert.AreEqual(1, methodResponses[2]![1]!["ids"]!.AsArray().Count);
        var semanticId = methodResponses[3]![1]!["ids"]![0]!.GetValue<string>();
        var localizedId = methodResponses[4]![1]!["ids"]![0]!.GetValue<string>();

        var get = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "ContactCard/get",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["ids"] = new JsonArray(semanticId, localizedId),
                },
                "get")),
        });
        var projected = get["methodResponses"]![0]![1]!["list"]!.AsArray();
        var semanticProjection = projected.Single(card =>
            card!["uid"]!.GetValue<string>() == "indexed-semantic-uid")!;
        Assert.AreEqual("Indexed Semantic", semanticProjection["name"]!["full"]!.GetValue<string>());
        var localizedProjection = projected.Single(card =>
            card!["uid"]!.GetValue<string>() == "indexed-localized-uid")!;
        Assert.IsNull(localizedProjection["localizations"]);
        Assert.IsNull(localizedProjection["media"]);

        JsonArray Query(string callId, string uid) => new(
            "ContactCard/query",
            new JsonObject
            {
                ["accountId"] = fixture.AccountId,
                ["filter"] = new JsonObject { ["uid"] = uid },
            },
            callId);
    }

    [TestMethod]
    public async Task CardDavEmbeddedJsContactRejectsLeafPatchedLocalizedBlobIds()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var collection = fixture.AddressBookHomePath + "embedded-leaf-guards/";
        const string createAddressBook = """
            <D:mkcol xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop>
                <D:resourcetype><D:collection/><A:addressbook/></D:resourcetype>
                <D:displayname>Embedded leaf guards</D:displayname>
              </D:prop></D:set>
            </D:mkcol>
            """;
        using var createdAddressBook = await fixture.SendAsync("MKCOL", collection, createAddressBook);
        Assert.AreEqual(HttpStatusCode.Created, createdAddressBook.StatusCode);

        const string safeUid = "leaf-safe-uid";
        const string safeBaseUri = "https://example.net/safe.png";
        const string safeLocalizedUri = "https://example.net/safe-fr.png";
        var safeEmbedded = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = safeUid,
            ["kind"] = "individual",
            ["name"] = new JsonObject { ["full"] = "Leaf safe" },
            ["media"] = new JsonObject
            {
                ["photo"] = new JsonObject
                {
                    ["kind"] = "photo",
                    ["uri"] = safeBaseUri,
                },
            },
            ["localizations"] = new JsonObject
            {
                ["fr"] = new JsonObject { ["media/photo/uri"] = safeLocalizedUri },
            },
        };
        using var safePut = await fixture.SendAsync(
            "PUT",
            collection + "safe.vcf",
            ForgedVCard(
                safeUid,
                "Leaf safe",
                safeEmbedded,
                includeProdId: true,
                additionalCoreLines: [$"PHOTO;PROP-ID=photo:{safeBaseUri}"]),
            "text/vcard; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, safePut.StatusCode);

        var foreignBlobId = await fixture.StoreBlobAsync([0x47, 0x49, 0x46], "image/gif");
        var expiredBlobId = await fixture.StoreBlobAsync([0x89, 0x50, 0x4e, 0x47], "image/png");
        var wrongTypeBlobId = await fixture.StoreBlobAsync(
            Encoding.ASCII.GetBytes("not an image"),
            "application/pdf");
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var foreign = await database.JmapBlobs.SingleAsync(blob => blob.BlobId == foreignBlobId);
            foreign.AccountId = Guid.CreateVersion7();
            var expired = await database.JmapBlobs.SingleAsync(blob => blob.BlobId == expiredBlobId);
            expired.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["missing"] = JmapId.UploadedBlob(Guid.CreateVersion7()),
            ["foreign"] = foreignBlobId,
            ["expired"] = expiredBlobId,
            ["wrong-type"] = wrongTypeBlobId,
        };
        foreach (var item in cases)
        {
            var uid = $"leaf-{item.Key}-uid";
            var fullName = $"Leaf {item.Key}";
            var baseUri = $"https://example.net/{item.Key}.png";
            var embedded = new JsonObject
            {
                ["@type"] = "Card",
                ["version"] = "1.0",
                ["uid"] = uid,
                ["kind"] = "individual",
                ["name"] = new JsonObject { ["full"] = fullName },
                ["media"] = new JsonObject
                {
                    ["photo"] = new JsonObject
                    {
                        ["kind"] = "photo",
                        ["uri"] = baseUri,
                    },
                },
                ["localizations"] = new JsonObject
                {
                    ["fr"] = new JsonObject
                    {
                        ["media/photo/uri"] = null,
                        ["media/photo/blobId"] = item.Value,
                    },
                },
            };
            using var put = await fixture.SendAsync(
                "PUT",
                collection + item.Key + ".vcf",
                ForgedVCard(
                    uid,
                    fullName,
                    embedded,
                    includeProdId: true,
                    additionalCoreLines: [$"PHOTO;PROP-ID=photo:{baseUri}"]),
                "text/vcard; charset=utf-8");
            Assert.AreEqual(HttpStatusCode.Created, put.StatusCode, item.Key);
        }

        var query = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "ContactCard/query",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                },
                "query")),
        });
        var ids = query["methodResponses"]![0]![1]!["ids"]!.AsArray();
        Assert.AreEqual(cases.Count + 1, ids.Count);

        var get = await fixture.SendJmapAsync(new JsonObject
        {
            ["using"] = new JsonArray(JmapConstants.CoreCapability, JmapConstants.ContactsCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "ContactCard/get",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["ids"] = ids.DeepClone(),
                },
                "get")),
        });
        var cards = get["methodResponses"]![0]![1]!["list"]!.AsArray();
        Assert.AreEqual(cases.Count + 1, cards.Count);
        var safeCard = cards.Single(value => value!["uid"]!.GetValue<string>() == safeUid)!;
        Assert.AreEqual(
            safeLocalizedUri,
            safeCard["localizations"]!["fr"]!["media/photo/uri"]!.GetValue<string>());
        foreach (var item in cases)
        {
            var uid = $"leaf-{item.Key}-uid";
            var card = cards.Single(value => value!["uid"]!.GetValue<string>() == uid)!;
            Assert.IsNull(card["localizations"], item.Key);
            var photo = card["media"]!["photo"]!;
            Assert.AreEqual($"https://example.net/{item.Key}.png", photo["uri"]!.GetValue<string>());
            Assert.IsNull(photo["blobId"], item.Key);
        }
    }

    private static string ForgedVCard(
        string uid,
        string fullName,
        JsonObject embedded,
        bool includeProdId = false,
        IReadOnlyList<string>? additionalCoreLines = null)
    {
        var core = new List<string> { "BEGIN:VCARD", "VERSION:4.0" };
        if (includeProdId)
            core.Add("PRODID:-//mk8.email//JMAP Contacts 1.0//EN");
        core.Add($"UID;VALUE=text:{uid}");
        core.Add("KIND:individual");
        core.Add($"FN:{fullName}");
        if (additionalCoreLines is not null)
            core.AddRange(additionalCoreLines);
        core.Add("END:VCARD");
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\r\n", core) + "\r\n")));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                embedded.ToJsonString(JmapJson.SerializerOptions)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        core.Insert(core.Count - 1, "X-MK8-JSCONTACT-HASH:" + hash);
        core.Insert(core.Count - 1, "X-MK8-JSCONTACT:" + encoded);
        return string.Join("\r\n", core) + "\r\n";
    }

    [TestMethod]
    public async Task DavAclSharesCalendarsAndAddressBooksWithEnforcedPrivileges()
    {
        await using var fixture = await DavFixture.CreateAsync();

        using var searchableProperties = await fixture.SendAsync(
            "REPORT",
            "/dav/principals/",
            "<D:principal-search-property-set xmlns:D=\"DAV:\"/>",
            headers: Header("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.OK, searchableProperties.StatusCode);
        Assert.IsTrue((await ReadXmlAsync(searchableProperties))
            .Descendants(Dav + "displayname").Any());

        const string principalSearch = """
        <D:principal-property-search xmlns:D="DAV:">
          <D:property-search>
            <D:prop><D:displayname/></D:prop>
            <D:match>attendee</D:match>
          </D:property-search>
          <D:prop><D:displayname/><D:principal-URL/></D:prop>
        </D:principal-property-search>
        """;
        using var searched = await fixture.SendAsync(
            "REPORT",
            "/dav/principals/",
            principalSearch,
            headers: Header("Depth", "0"));
        Assert.AreEqual((HttpStatusCode)207, searched.StatusCode);
        var searchXml = await ReadXmlAsync(searched);
        CollectionAssert.AreEqual(
            new[] { fixture.AttendeePrincipalPath },
            Hrefs(searchXml));

        var calendarCollection = fixture.CalendarHomePath + "team/";
        const string createCalendar = """
        <C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:set><D:prop><D:displayname>Shared team calendar</D:displayname></D:prop></D:set>
        </C:mkcalendar>
        """;
        using var createdCalendar = await fixture.SendAsync(
            "MKCALENDAR",
            calendarCollection,
            createCalendar);
        Assert.AreEqual(HttpStatusCode.Created, createdCalendar.StatusCode);

        const string eventOne = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Shared DAV tests//EN
        BEGIN:VEVENT
        UID:shared-one@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART:20261002T100000Z
        DTEND:20261002T110000Z
        SUMMARY:Shared event one
        END:VEVENT
        END:VCALENDAR
        """;
        using var createdEvent = await fixture.SendAsync(
            "PUT",
            calendarCollection + "one.ics",
            eventOne,
            "text/calendar; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, createdEvent.StatusCode);

        using var grantedRead = await fixture.SendAsync(
            "ACL",
            calendarCollection,
            Acl(fixture.AttendeePrincipalPath, writable: false));
        Assert.AreEqual(HttpStatusCode.OK, grantedRead.StatusCode);

        using var crossTenantGrant = await fixture.SendAsync(
            "ACL",
            calendarCollection,
            Acl(fixture.OutsiderPrincipalPath, writable: false));
        Assert.AreEqual(HttpStatusCode.Forbidden, crossTenantGrant.StatusCode);
        Assert.IsTrue((await ReadXmlAsync(crossTenantGrant))
            .Descendants(Dav + "allowed-principal").Any());

        using var ownerAcl = await fixture.SendAsync(
            "PROPFIND",
            calendarCollection,
            Propfind("<D:acl/><D:acl-restrictions/><D:current-user-privilege-set/>"),
            headers: Header("Depth", "0"));
        Assert.AreEqual((HttpStatusCode)207, ownerAcl.StatusCode);
        var ownerAclXml = await ReadXmlAsync(ownerAcl);
        Assert.IsTrue(ownerAclXml.Descendants(Dav + "protected").Any());
        Assert.IsTrue(ownerAclXml.Descendants(Dav + "grant-only").Any());
        Assert.IsTrue(ownerAclXml.Descendants(Dav + "href")
            .Any(element => element.Value == fixture.AttendeePrincipalPath));
        Assert.IsTrue(ownerAclXml.Descendants(Dav + "write-acl").Any());

        using var attendeeCalendars = await fixture.SendAsAttendeeAsync(
            "PROPFIND",
            fixture.AttendeeCalendarHomePath,
            Propfind("<D:displayname/><D:resourcetype/><D:owner/><D:current-user-privilege-set/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, attendeeCalendars.StatusCode);
        var attendeeCalendarXml = await ReadXmlAsync(attendeeCalendars);
        var sharedCalendarResponse = ResponseWithDisplayName(
            attendeeCalendarXml,
            "Shared team calendar");
        var sharedCalendarHref = sharedCalendarResponse.Element(Dav + "href")!.Value;
        StringAssert.StartsWith(sharedCalendarHref, fixture.AttendeeCalendarHomePath + "shared-");
        Assert.AreEqual(
            fixture.PrincipalPath,
            sharedCalendarResponse.Descendants(Dav + "owner").Single()
                .Element(Dav + "href")?.Value);
        Assert.IsTrue(sharedCalendarResponse.Descendants(Dav + "read").Any());
        Assert.IsFalse(sharedCalendarResponse.Descendants(Dav + "write").Any());

        using var attendeeRead = await fixture.SendAsAttendeeAsync(
            "GET",
            sharedCalendarHref + "one.ics");
        Assert.AreEqual(HttpStatusCode.OK, attendeeRead.StatusCode);
        StringAssert.Contains(await attendeeRead.Content.ReadAsStringAsync(), "Shared event one");

        const string eventTwo = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Shared DAV tests//EN
        BEGIN:VEVENT
        UID:shared-two@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART:20261003T100000Z
        DTEND:20261003T110000Z
        SUMMARY:Shared event two
        END:VEVENT
        END:VCALENDAR
        """;
        using var readOnlyWrite = await fixture.SendAsAttendeeAsync(
            "PUT",
            sharedCalendarHref + "two.ics",
            eventTwo,
            "text/calendar; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Forbidden, readOnlyWrite.StatusCode);

        using var grantedWrite = await fixture.SendAsync(
            "ACL",
            calendarCollection,
            Acl(fixture.AttendeePrincipalPath, writable: true));
        Assert.AreEqual(HttpStatusCode.OK, grantedWrite.StatusCode);
        const string sharedPropertyPatch = """
        <D:propertyupdate xmlns:D="DAV:">
          <D:set><D:prop><D:displayname>Shared team calendar updated</D:displayname></D:prop></D:set>
        </D:propertyupdate>
        """;
        using var attendeePropertyWrite = await fixture.SendAsAttendeeAsync(
            "PROPPATCH",
            sharedCalendarHref,
            sharedPropertyPatch);
        Assert.AreEqual((HttpStatusCode)207, attendeePropertyWrite.StatusCode);
        using var attendeeWrite = await fixture.SendAsAttendeeAsync(
            "PUT",
            sharedCalendarHref + "two.ics",
            eventTwo,
            "text/calendar; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, attendeeWrite.StatusCode);
        using var ownerReadsSharedWrite = await fixture.SendAsync(
            "GET",
            calendarCollection + "two.ics");
        Assert.AreEqual(HttpStatusCode.OK, ownerReadsSharedWrite.StatusCode);

        var addressBookCollection = fixture.AddressBookHomePath + "directory-shared/";
        const string createAddressBook = """
        <D:mkcol xmlns:D="DAV:" xmlns:A="urn:ietf:params:xml:ns:carddav">
          <D:set><D:prop>
            <D:resourcetype><D:collection/><A:addressbook/></D:resourcetype>
            <D:displayname>Shared directory</D:displayname>
          </D:prop></D:set>
        </D:mkcol>
        """;
        using var createdAddressBook = await fixture.SendAsync(
            "MKCOL",
            addressBookCollection,
            createAddressBook);
        Assert.AreEqual(HttpStatusCode.Created, createdAddressBook.StatusCode);
        using var sharedAddressBook = await fixture.SendAsync(
            "ACL",
            addressBookCollection,
            Acl(fixture.AttendeePrincipalPath, writable: true));
        Assert.AreEqual(HttpStatusCode.OK, sharedAddressBook.StatusCode);

        using var attendeeAddressBooks = await fixture.SendAsAttendeeAsync(
            "PROPFIND",
            fixture.AttendeeAddressBookHomePath,
            Propfind("<D:displayname/><D:resourcetype/><D:current-user-privilege-set/>"),
            headers: Header("Depth", "1"));
        var attendeeAddressBookXml = await ReadXmlAsync(attendeeAddressBooks);
        var sharedAddressBookHref = ResponseWithDisplayName(
            attendeeAddressBookXml,
            "Shared directory").Element(Dav + "href")!.Value;

        const string contact = """
        BEGIN:VCARD
        VERSION:4.0
        UID;VALUE=text:shared-contact@example.net
        FN:Shared Contact
        EMAIL:shared-contact@example.net
        END:VCARD
        """;
        using var attendeeContactWrite = await fixture.SendAsAttendeeAsync(
            "PUT",
            sharedAddressBookHref + "shared.vcf",
            contact,
            "text/vcard; charset=utf-8");
        Assert.AreEqual(HttpStatusCode.Created, attendeeContactWrite.StatusCode);
        using var ownerReadsContact = await fixture.SendAsync(
            "GET",
            addressBookCollection + "shared.vcf");
        Assert.AreEqual(HttpStatusCode.OK, ownerReadsContact.StatusCode);

        using var attendeeUnsubscribes = await fixture.SendAsAttendeeAsync(
            "DELETE",
            sharedAddressBookHref);
        Assert.AreEqual(HttpStatusCode.NoContent, attendeeUnsubscribes.StatusCode);
        using var ownerStillReadsContact = await fixture.SendAsync(
            "GET",
            addressBookCollection + "shared.vcf");
        Assert.AreEqual(HttpStatusCode.OK, ownerStillReadsContact.StatusCode);
        using var removedBinding = await fixture.SendAsAttendeeAsync(
            "GET",
            sharedAddressBookHref + "shared.vcf");
        Assert.AreEqual(HttpStatusCode.NotFound, removedBinding.StatusCode);

        using var revokedCalendar = await fixture.SendAsync(
            "ACL",
            calendarCollection,
            "<D:acl xmlns:D=\"DAV:\"/>");
        Assert.AreEqual(HttpStatusCode.OK, revokedCalendar.StatusCode);
        using var revokedRead = await fixture.SendAsAttendeeAsync(
            "GET",
            sharedCalendarHref + "one.ics");
        Assert.AreEqual(HttpStatusCode.NotFound, revokedRead.StatusCode);
    }

    [TestMethod]
    public async Task CalDavSchedulingDeliversLocalInvitationsRepliesAndExternalImip()
    {
        await using var fixture = await DavFixture.CreateAsync();
        const string external = "external.attendee@example.net";
        var invitation = $$"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Scheduling tests//EN
        METHOD:REQUEST
        BEGIN:VEVENT
        UID:scheduling-invitation-1@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART:20261001T100000Z
        DTEND:20261001T110000Z
        ORGANIZER:mailto:{{fixture.PrimaryAddress}}
        ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:{{fixture.AttendeeAddress}}
        ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:{{external}}
        SUMMARY:Scheduling delivery
        END:VEVENT
        END:VCALENDAR
        """;
        using var sent = await fixture.SendAsync(
            "POST",
            fixture.SchedulingOutboxPath,
            invitation,
            "text/calendar; charset=utf-8; method=REQUEST",
            new Dictionary<string, string>
            {
                ["Originator"] = $"mailto:{fixture.PrimaryAddress}",
                ["Recipient"] = $"mailto:{fixture.AttendeeAddress}, mailto:{external}",
            });
        Assert.AreEqual(HttpStatusCode.OK, sent.StatusCode);
        var sentXml = await ReadXmlAsync(sent);
        Assert.AreEqual(2, sentXml.Descendants(CalDav + "response").Count());
        Assert.IsTrue(sentXml.Descendants(CalDav + "request-status")
            .All(status => status.Value.StartsWith("2.0", StringComparison.Ordinal)));

        using var attendeeInbox = await fixture.SendAsAttendeeAsync(
            "PROPFIND",
            fixture.AttendeeSchedulingInboxPath,
            Propfind("<D:resourcetype/><D:getetag/><C:calendar-data/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, attendeeInbox.StatusCode);
        var attendeeInboxXml = await ReadXmlAsync(attendeeInbox);
        Assert.IsNotNull(attendeeInboxXml.Descendants(CalDav + "schedule-inbox").SingleOrDefault());
        var invitationPath = Hrefs(attendeeInboxXml)
            .Single(href => href.EndsWith(".ics", StringComparison.Ordinal));
        using var storedInvitation = await fixture.SendAsAttendeeAsync("GET", invitationPath);
        Assert.AreEqual(HttpStatusCode.OK, storedInvitation.StatusCode);
        StringAssert.Contains(
            await storedInvitation.Content.ReadAsStringAsync(),
            "METHOD:REQUEST");

        var queued = fixture.QueuedSubmissions.Single();
        Assert.AreEqual(fixture.PrimaryAddress, queued.EnvelopeSender);
        Assert.AreEqual(external, queued.Recipients.Single().Address);
        Assert.IsFalse(queued.Recipients.Single().IsLocal);
        StringAssert.Contains(queued.RawMessage, "METHOD:REQUEST");
        StringAssert.Contains(queued.RawMessage, "text/calendar");

        var reply = $$"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Scheduling tests//EN
        METHOD:REPLY
        BEGIN:VEVENT
        UID:scheduling-invitation-1@mk8n.com
        DTSTAMP:20260921T081500Z
        DTSTART:20261001T100000Z
        DTEND:20261001T110000Z
        ORGANIZER:mailto:{{fixture.PrimaryAddress}}
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:{{fixture.AttendeeAddress}}
        SUMMARY:Scheduling delivery
        END:VEVENT
        END:VCALENDAR
        """;
        using var replied = await fixture.SendAsAttendeeAsync(
            "POST",
            fixture.AttendeeCalendarHomePath + "schedule-outbox/",
            reply,
            "text/calendar; charset=utf-8; method=REPLY",
            new Dictionary<string, string>
            {
                ["Originator"] = $"mailto:{fixture.AttendeeAddress}",
                ["Recipient"] = $"mailto:{fixture.PrimaryAddress}",
            });
        Assert.AreEqual(HttpStatusCode.OK, replied.StatusCode);

        using var organizerInbox = await fixture.SendAsync(
            "PROPFIND",
            fixture.SchedulingInboxPath,
            Propfind("<D:getetag/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, organizerInbox.StatusCode);
        var replyPath = Hrefs(await ReadXmlAsync(organizerInbox))
            .Single(href => href.EndsWith(".ics", StringComparison.Ordinal));
        using var storedReply = await fixture.SendAsync("GET", replyPath);
        Assert.AreEqual(HttpStatusCode.OK, storedReply.StatusCode);
        StringAssert.Contains(
            await storedReply.Content.ReadAsStringAsync(),
            "PARTSTAT=ACCEPTED");

        using var forged = await fixture.SendAsync(
            "POST",
            fixture.SchedulingOutboxPath,
            invitation,
            "text/calendar; method=REQUEST",
            new Dictionary<string, string>
            {
                ["Originator"] = "mailto:forged@mk8n.com",
                ["Recipient"] = $"mailto:{fixture.AttendeeAddress}",
            });
        Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.AreEqual(1, fixture.QueuedSubmissions.Count);
    }

    [TestMethod]
    public async Task CalDavSchedulingAnswersRecurrenceAwareFreeBusyQueries()
    {
        await using var fixture = await DavFixture.CreateAsync();
        using var discover = await fixture.SendAsAttendeeAsync(
            "PROPFIND",
            fixture.AttendeeCalendarHomePath,
            Propfind("<D:displayname/>"),
            headers: Header("Depth", "1"));
        Assert.AreEqual((HttpStatusCode)207, discover.StatusCode);

        const string recurringEvent = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Scheduling tests//EN
        BEGIN:VEVENT
        UID:recurring-busy-1@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART:20261001T100000Z
        DTEND:20261001T110000Z
        RRULE:FREQ=WEEKLY;COUNT=3
        EXDATE:20261008T100000Z
        SUMMARY:Recurring busy event
        END:VEVENT
        END:VCALENDAR
        """;
        using var created = await fixture.SendAsAttendeeAsync(
            "PUT",
            fixture.AttendeeCalendarHomePath + "default/recurring.ics",
            recurringEvent,
            "text/calendar; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        const string daylightSavingEvent = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Scheduling tests//EN
        BEGIN:VEVENT
        UID:daylight-saving-busy-1@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART;TZID=Europe/Zagreb:20261018T100000
        DTEND;TZID=Europe/Zagreb:20261018T110000
        RRULE:FREQ=WEEKLY;COUNT=2
        SUMMARY:Local-time recurring event
        END:VEVENT
        END:VCALENDAR
        """;
        using var createdDaylightSaving = await fixture.SendAsAttendeeAsync(
            "PUT",
            fixture.AttendeeCalendarHomePath + "default/daylight-saving.ics",
            daylightSavingEvent,
            "text/calendar; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, createdDaylightSaving.StatusCode);

        const string lastWeekdayEvent = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Scheduling tests//EN
        BEGIN:VEVENT
        UID:last-weekday-busy-1@mk8n.com
        DTSTAMP:20260921T080000Z
        DTSTART:20260930T140000Z
        DTEND:20260930T143000Z
        RRULE:FREQ=MONTHLY;COUNT=2;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1
        SUMMARY:Last weekday recurrence
        END:VEVENT
        END:VCALENDAR
        """;
        using var createdLastWeekday = await fixture.SendAsAttendeeAsync(
            "PUT",
            fixture.AttendeeCalendarHomePath + "default/last-weekday.ics",
            lastWeekdayEvent,
            "text/calendar; charset=utf-8",
            Header("If-None-Match", "*"));
        Assert.AreEqual(HttpStatusCode.Created, createdLastWeekday.StatusCode);

        var freeBusyRequest = $$"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//mk8.email//Scheduling tests//EN
        METHOD:REQUEST
        BEGIN:VFREEBUSY
        UID:freebusy-request-1@mk8n.com
        DTSTAMP:20260921T090000Z
        DTSTART:20261001T000000Z
        DTEND:20261101T000000Z
        ORGANIZER:mailto:{{fixture.PrimaryAddress}}
        ATTENDEE:mailto:{{fixture.AttendeeAddress}}
        END:VFREEBUSY
        END:VCALENDAR
        """;
        using var response = await fixture.SendAsync(
            "POST",
            fixture.SchedulingOutboxPath,
            freeBusyRequest,
            "text/calendar; charset=utf-8; method=REQUEST",
            new Dictionary<string, string>
            {
                ["Originator"] = $"mailto:{fixture.PrimaryAddress}",
                ["Recipient"] = $"mailto:{fixture.AttendeeAddress}",
            });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var responseXml = await ReadXmlAsync(response);
        Assert.AreEqual(
            "2.0;Success",
            responseXml.Descendants(CalDav + "request-status").Single().Value);
        var freeBusy = responseXml.Descendants(CalDav + "calendar-data").Single().Value;
        StringAssert.Contains(freeBusy, "METHOD:REPLY");
        StringAssert.Contains(freeBusy, "FREEBUSY;FBTYPE=BUSY:20261001T100000Z/20261001T110000Z");
        StringAssert.Contains(freeBusy, "FREEBUSY;FBTYPE=BUSY:20261015T100000Z/20261015T110000Z");
        StringAssert.Contains(freeBusy, "FREEBUSY;FBTYPE=BUSY:20261018T080000Z/20261018T090000Z");
        StringAssert.Contains(freeBusy, "FREEBUSY;FBTYPE=BUSY:20261025T090000Z/20261025T100000Z");
        StringAssert.Contains(freeBusy, "FREEBUSY;FBTYPE=BUSY:20261030T140000Z/20261030T143000Z");
        Assert.IsFalse(freeBusy.Contains("20261008T100000Z", StringComparison.Ordinal));
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

    private static string Acl(string principalHref, bool writable) => $$"""
        <D:acl xmlns:D="DAV:">
          <D:ace>
            <D:principal><D:href>{{principalHref}}</D:href></D:principal>
            <D:grant>
              <D:privilege><D:read/></D:privilege>
              {{(writable ? "<D:privilege><D:write/></D:privilege>" : string.Empty)}}
            </D:grant>
          </D:ace>
        </D:acl>
        """;

    private static XElement ResponseWithDisplayName(XDocument document, string displayName) =>
        document.Descendants(Dav + "response").Single(response =>
            response.Descendants(Dav + "displayname")
                .Any(element => element.Value == displayName));

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response) =>
        XDocument.Parse(await response.Content.ReadAsStringAsync());

    private static string[] Hrefs(XDocument document) => document
        .Descendants(Dav + "response")
        .Select(response => response.Element(Dav + "href")?.Value)
        .Where(value => value is not null)
        .Cast<string>()
        .ToArray();
}
