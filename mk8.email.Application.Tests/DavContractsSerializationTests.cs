using System.Text.Json;
using mk8.email.Dav;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DavContractsSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void DavCollectionAndContentSurviveApplicationTransportSerialization()
    {
        var owner = Guid.CreateVersion7();
        var collection = new DavCollection(
            Guid.CreateVersion7(), owner, DavCollectionAccess.Owner, owner, "calendar",
            [new DavShareGrant(Guid.CreateVersion7(), DavCollectionAccess.ReadOnly)],
            DavCollectionKind.Calendar, "calendar", "Calendar", "Description", "#123456",
            2, ["VEVENT", "VTODO"], 17, DateTime.UtcNow, DateTime.UtcNow);
        var content = new DavContentInfo(
            "calendar-uid", "text/calendar", new HashSet<string>(StringComparer.Ordinal) { "VEVENT" },
            "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["UID"] = ["calendar-uid"] });

        var restoredCollection = JsonSerializer.Deserialize<DavCollection>(
            JsonSerializer.Serialize(collection, JsonOptions), JsonOptions);
        var restoredContent = JsonSerializer.Deserialize<DavContentInfo>(
            JsonSerializer.Serialize(content, JsonOptions), JsonOptions);

        Assert.IsNotNull(restoredCollection);
        Assert.AreEqual(collection.Id, restoredCollection.Id);
        Assert.AreEqual(collection.HrefSlug, restoredCollection.HrefSlug, StringComparer.Ordinal);
        Assert.AreEqual(DavCollectionAccess.ReadOnly, restoredCollection.Shares[0].Access);
        Assert.IsTrue(restoredCollection.CanWrite);
        Assert.IsNotNull(restoredContent);
        Assert.AreEqual(content.Uid, restoredContent.Uid, StringComparer.Ordinal);
        CollectionAssert.Contains(restoredContent.Components.ToArray(), "VEVENT");
        Assert.AreEqual("calendar-uid", restoredContent.Properties["UID"][0], StringComparer.Ordinal);
    }

    [TestMethod]
    public void DavContentRejectsNullInputBeforeParsing()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            DavContent.TryValidate(DavCollectionKind.AddressBook, "text/vcard", null!, out _, out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => DavContent.UnfoldLines(null!));
    }
}
