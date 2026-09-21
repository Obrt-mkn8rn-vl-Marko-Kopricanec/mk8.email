using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Dav;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapContactCodecTests
{
    [TestMethod]
    public void EncodeUsesRfc6350ValueTypesAndRfc9555StructuredMappings()
    {
        var card = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "family,plain",
            ["kind"] = "group",
            ["name"] = new JsonObject
            {
                ["full"] = "Dr. John Philip Paul Stevenson, Jr., M.D.",
                ["components"] = new JsonArray(
                    new JsonObject { ["kind"] = "surname", ["value"] = "Stevenson" },
                    new JsonObject { ["kind"] = "given", ["value"] = "John" },
                    new JsonObject { ["kind"] = "given2", ["value"] = "Philip" },
                    new JsonObject { ["kind"] = "given2", ["value"] = "Paul" },
                    new JsonObject { ["kind"] = "title", ["value"] = "Dr." },
                    new JsonObject { ["kind"] = "credential", ["value"] = "M.D." },
                    new JsonObject { ["kind"] = "credential", ["value"] = "A.C.P." },
                    new JsonObject { ["kind"] = "generation", ["value"] = "Jr." }),
                ["isOrdered"] = false,
            },
            ["organizations"] = new JsonObject
            {
                ["work"] = new JsonObject
                {
                    ["name"] = "ABC, Inc.",
                    ["units"] = new JsonArray(
                        new JsonObject { ["name"] = "North American Division" },
                        new JsonObject { ["name"] = "Marketing" }),
                },
            },
            ["members"] = new JsonObject
            {
                ["local-member"] = true,
                ["urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af"] = true,
            },
        };

        Assert.IsTrue(JmapContactCodec.TryValidate(card, out var invalid),
            string.Join(", ", invalid));
        var encoded = Encoding.UTF8.GetString(JmapContactCodec.Encode(card));

        StringAssert.Contains(encoded, "UID;VALUE=text:family\\,plain\r\n");
        StringAssert.Contains(
            encoded,
            "N:Stevenson;John;Philip,Paul;Dr.;M.D.,A.C.P.,Jr.;;Jr.\r\n");
        StringAssert.Contains(encoded, "ORG:ABC\\, Inc.;North American Division;Marketing\r\n");
        StringAssert.Contains(
            encoded,
            "MEMBER:urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af\r\n");
        Assert.IsFalse(encoded.Contains("MEMBER:local-member", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DecodeProjectsPublishedNameAndOrganizationShapesAndAlwaysValidatesFallback()
    {
        var resource = Resource("published-name", """
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:published-name
            KIND:group
            FN:Dr. John Philip Stevenson Jr.
            N:Stevenson;John;Philip,Paul;Dr.;Jr.,M.D.,A.C.P.;;Jr.
            ORG:ABC\, Inc.;North American Division;Marketing
            MEMBER:urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af
            IMPP:not a uri
            END:VCARD
            """);

        var decoded = JmapContactCodec.Decode(resource);

        Assert.IsTrue(JmapContactCodec.TryValidate(decoded, out var invalid),
            string.Join(", ", invalid));
        var components = decoded["name"]!["components"]!.AsArray()
            .Select(component => $"{component!["kind"]!.GetValue<string>()}:{component["value"]!.GetValue<string>()}")
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "surname:Stevenson", "given:John", "given2:Philip", "given2:Paul",
                "title:Dr.", "credential:M.D.", "credential:A.C.P.", "generation:Jr.",
            },
            components);
        var units = decoded["organizations"]!.AsObject().Single().Value!["units"]!.AsArray();
        CollectionAssert.AreEqual(
            new[] { "North American Division", "Marketing" },
            units.Select(unit => unit!["name"]!.GetValue<string>()).ToArray());
        Assert.AreEqual(
            true,
            decoded["members"]!["urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af"]!.GetValue<bool>());
        Assert.IsNull(decoded["onlineServices"], "The invalid IMPP projection must be removed before exposure.");
    }

    [TestMethod]
    public void DavValidationEnforcesUidAndMemberValueSemantics()
    {
        Assert.IsTrue(Validate("""
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:plain-uid
            KIND:group
            FN:Valid Group
            MEMBER:urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af
            END:VCARD
            """));
        Assert.IsFalse(Validate("""
            BEGIN:VCARD
            VERSION:4.0
            UID:plain-uid
            FN:Invalid UID
            END:VCARD
            """));
        Assert.IsFalse(Validate("""
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:group-uid
            KIND:group
            FN:Invalid Member
            MEMBER:relative-member
            END:VCARD
            """));
        Assert.IsFalse(Validate("""
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:person-uid
            KIND:individual
            FN:Invalid Person Member
            MEMBER:urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af
            END:VCARD
            """));
        Assert.IsFalse(Validate("""
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:duplicate
            UID;VALUE=text:duplicate
            FN:Duplicate UID
            END:VCARD
            """));
    }

    private static bool Validate(string text) => DavContent.TryValidate(
        DavCollectionKind.AddressBook,
        "text/vcard",
        Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)),
        out _,
        out _);

    private static DavResourceDB Resource(string uid, string text) => new()
    {
        Id = Guid.CreateVersion7(),
        CollectionId = Guid.CreateVersion7(),
        ResourceName = uid + ".vcf",
        Uid = uid,
        ContentType = "text/vcard",
        Content = Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)),
        CreatedAt = new DateTime(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc),
    };
}
