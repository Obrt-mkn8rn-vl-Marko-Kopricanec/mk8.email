using System.Security.Cryptography;
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
        Assert.IsFalse(encoded.Contains("FN;DERIVED=", StringComparison.Ordinal));
        StringAssert.Contains(
            encoded,
            "N:Stevenson;John;Philip,Paul;Dr.;M.D.,A.C.P.,Jr.;;Jr.\r\n");
        StringAssert.Contains(
            encoded,
            "ORG;PROP-ID=work:ABC\\, Inc.;North American Division;Marketing\r\n");
        StringAssert.Contains(
            encoded,
            "MEMBER:urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af\r\n");
        Assert.IsFalse(encoded.Contains("MEMBER:local-member", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rfc9555PropIdsRoundTripAndSynthesizedFullNameIsDerived()
    {
        var card = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "prop-id-roundtrip",
            ["kind"] = "individual",
            ["name"] = new JsonObject
            {
                ["components"] = new JsonArray(
                    new JsonObject { ["kind"] = "given", ["value"] = "Jane" },
                    new JsonObject { ["kind"] = "surname", ["value"] = "Doe" }),
                ["isOrdered"] = true,
            },
            ["nicknames"] = new JsonObject
            {
                ["nick_primary"] = new JsonObject { ["name"] = "Janie" },
            },
            ["organizations"] = new JsonObject
            {
                ["org_main"] = new JsonObject { ["name"] = "Example Corp" },
            },
            ["titles"] = new JsonObject
            {
                ["title_primary"] = new JsonObject { ["name"] = "Engineer", ["kind"] = "title" },
            },
            ["emails"] = new JsonObject
            {
                ["email_work"] = new JsonObject { ["address"] = "jane@example.net" },
            },
            ["phones"] = new JsonObject
            {
                ["phone_mobile"] = new JsonObject { ["number"] = "+1-555-0100" },
            },
            ["onlineServices"] = new JsonObject
            {
                ["chat_main"] = new JsonObject { ["uri"] = "xmpp:jane@example.net" },
            },
            ["addresses"] = new JsonObject
            {
                ["address_home"] = new JsonObject
                {
                    ["components"] = new JsonArray(
                        new JsonObject { ["kind"] = "name", ["value"] = "1 Main Street" },
                        new JsonObject { ["kind"] = "locality", ["value"] = "Exampleville" }),
                },
            },
            ["links"] = new JsonObject
            {
                ["link_profile"] = new JsonObject { ["uri"] = "https://example.net/jane" },
            },
            ["media"] = new JsonObject
            {
                ["photo_primary"] = new JsonObject
                {
                    ["kind"] = "photo",
                    ["uri"] = "https://example.net/jane.jpg",
                },
            },
            ["notes"] = new JsonObject
            {
                ["note_main"] = new JsonObject { ["note"] = "Primary note" },
            },
            ["anniversaries"] = new JsonObject
            {
                ["birth_date"] = new JsonObject
                {
                    ["kind"] = "birth",
                    ["date"] = new JsonObject
                    {
                        ["@type"] = "PartialDate",
                        ["year"] = 1990,
                        ["month"] = 1,
                        ["day"] = 2,
                    },
                },
            },
        };

        Assert.IsTrue(JmapContactCodec.TryValidate(card, out var invalid),
            string.Join(", ", invalid));
        var encoded = Encoding.UTF8.GetString(JmapContactCodec.Encode(card));

        StringAssert.Contains(encoded, "FN;DERIVED=TRUE:Jane Doe\r\n");
        StringAssert.Contains(encoded, "NICKNAME;PROP-ID=nick_primary:Janie\r\n");
        StringAssert.Contains(encoded, "ORG;PROP-ID=org_main:Example Corp\r\n");
        StringAssert.Contains(encoded, "TITLE;PROP-ID=title_primary:Engineer\r\n");
        StringAssert.Contains(encoded, "EMAIL;PROP-ID=email_work:jane@example.net\r\n");
        StringAssert.Contains(encoded, "TEL;PROP-ID=phone_mobile:+1-555-0100\r\n");
        StringAssert.Contains(encoded, "IMPP;PROP-ID=chat_main:xmpp:jane@example.net\r\n");
        StringAssert.Contains(encoded, "ADR;PROP-ID=address_home:");
        StringAssert.Contains(encoded, "URL;PROP-ID=link_profile:https://example.net/jane\r\n");
        StringAssert.Contains(encoded, "PHOTO;PROP-ID=photo_primary:https://example.net/jane.jpg\r\n");
        StringAssert.Contains(encoded, "NOTE;PROP-ID=note_main:Primary note\r\n");
        StringAssert.Contains(encoded, "BDAY;PROP-ID=birth_date:19900102\r\n");

        var decoded = Decode(Resource(
            "prop-id-roundtrip",
            WithoutEmbeddedExtensions(encoded)));
        Assert.IsNull(decoded["name"]!["full"]);
        foreach (var expected in new Dictionary<string, string>
        {
            ["nicknames"] = "nick_primary",
            ["organizations"] = "org_main",
            ["titles"] = "title_primary",
            ["emails"] = "email_work",
            ["phones"] = "phone_mobile",
            ["onlineServices"] = "chat_main",
            ["addresses"] = "address_home",
            ["links"] = "link_profile",
            ["media"] = "photo_primary",
            ["notes"] = "note_main",
            ["anniversaries"] = "birth_date",
        })
        {
            Assert.IsTrue(decoded[expected.Key]!.AsObject().ContainsKey(expected.Value), expected.Key);
        }
        StringAssert.Contains(
            Encoding.UTF8.GetString(JmapContactCodec.Encode(decoded)),
            "FN;DERIVED=TRUE:");
    }

    [TestMethod]
    public void DecodeResolvesDuplicatePropIdsWithoutOverwritingSiblingValues()
    {
        var decoded = Decode(Resource("duplicate-prop-id", """
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:duplicate-prop-id
            FN:Duplicate property identifiers
            EMAIL;PROP-ID=shared:first@example.net
            EMAIL;PROP-ID=shared:second@example.net
            TITLE;PROP-ID=shared:Engineer
            END:VCARD
            """));

        var emails = decoded["emails"]!.AsObject();
        Assert.AreEqual(2, emails.Count);
        Assert.AreEqual("first@example.net", emails["shared"]!["address"]!.GetValue<string>());
        Assert.IsTrue(emails.Any(item =>
            item.Value!["address"]!.GetValue<string>() == "second@example.net"));
        Assert.AreEqual("Engineer", decoded["titles"]!["shared"]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    public void DecodePreservesConsistentLegacyEmbeddedIdsUntilTheCardIsReencoded()
    {
        var embedded = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "legacy-embedded",
            ["kind"] = "individual",
            ["name"] = new JsonObject { ["full"] = "Legacy Embedded" },
            ["emails"] = new JsonObject
            {
                ["legacy_email"] = new JsonObject { ["address"] = "legacy@example.net" },
            },
            ["mk8.email:legacy"] = new JsonObject { ["preserved"] = true },
        };
        var core = new[]
        {
            "BEGIN:VCARD",
            "VERSION:4.0",
            "PRODID:-//mk8.email//JMAP Contacts 1.0//EN",
            "UID;VALUE=text:legacy-embedded",
            "KIND:individual",
            "FN:Legacy Embedded",
            "EMAIL:legacy@example.net",
            "END:VCARD",
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\r\n", core) + "\r\n")));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                embedded.ToJsonString(JmapJson.SerializerOptions)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var lines = core.ToList();
        lines.Insert(lines.Count - 1, "X-MK8-JSCONTACT-HASH:" + hash);
        lines.Insert(lines.Count - 1, "X-MK8-JSCONTACT:" + encoded);

        var decoded = Decode(Resource(
            "legacy-embedded",
            string.Join("\r\n", lines) + "\r\n"));

        Assert.IsTrue(decoded["emails"]!.AsObject().ContainsKey("legacy_email"));
        Assert.IsTrue(decoded["mk8.email:legacy"]!["preserved"]!.GetValue<bool>());
        StringAssert.Contains(
            Encoding.UTF8.GetString(JmapContactCodec.Encode(decoded)),
            "EMAIL;PROP-ID=legacy_email:legacy@example.net\r\n");
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

        var decoded = Decode(resource);

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
            UID;VALUE=text:empty-derived-name
            FN;DERIVED=TRUE:
            END:VCARD
            """));
        Assert.IsFalse(Validate("""
            BEGIN:VCARD
            VERSION:4.0
            UID;VALUE=text:missing-name
            END:VCARD
            """));
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

    private static string WithoutEmbeddedExtensions(string value)
    {
        var result = new List<string>();
        var skipping = false;
        foreach (var line in value.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("X-MK8-JSCONTACT", StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }
            if (skipping && line.StartsWith(' '))
                continue;
            skipping = false;
            result.Add(line);
        }
        return string.Join("\r\n", result) + "\r\n";
    }

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

    private static JsonObject Decode(DavResourceDB resource) =>
        JmapContactCodec.Decode(resource, resource.Content!);
}
