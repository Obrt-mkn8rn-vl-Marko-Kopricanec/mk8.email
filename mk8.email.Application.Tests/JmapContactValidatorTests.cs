using System.Text.Json.Nodes;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapContactValidatorTests
{
    [TestMethod]
    public void Rfc9553RegisteredObjectsAndEnumsAreValidatedRecursively()
    {
        var card = JsonNode.Parse(
            """
            {
              "@type": "Card",
              "version": "1.0",
              "uid": "urn:uuid:f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
              "created": "2022-09-30T14:35:10.003Z",
              "updated": "2022-10-01T10:00:00Z",
              "kind": "individual",
              "language": "zh-Hant",
              "prodId": "RFC 9553 vector",
              "relatedTo": {
                "urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af": {
                  "@type": "Relation",
                  "relation": { "friend": true, "example.com:mentor": true }
                }
              },
              "name": {
                "@type": "Name",
                "components": [
                  { "kind": "title", "value": "Dr." },
                  { "kind": "given", "value": "中山", "phonetic": "zung1saan1" },
                  { "kind": "surname", "value": "孫", "phonetic": "syun1" },
                  { "kind": "credential", "value": "Ph.D." },
                  { "kind": "generation", "value": "III" }
                ],
                "phoneticScript": "Latn",
                "phoneticSystem": "jyut",
                "sortAs": { "surname": "孫", "given": "中山" }
              },
              "nicknames": {
                "nick": { "@type": "Nickname", "name": "Sun", "contexts": { "private": true } }
              },
              "organizations": {
                "org": {
                  "@type": "Organization",
                  "name": "Example Corp",
                  "units": [
                    { "@type": "OrgUnit", "name": "Research" },
                    { "name": "Protocol Lab", "sortAs": "Lab" }
                  ],
                  "contexts": { "work": true }
                }
              },
              "speakToAs": {
                "@type": "SpeakToAs",
                "grammaticalGender": "neuter",
                "pronouns": {
                  "p1": { "@type": "Pronouns", "pronouns": "they/them", "pref": 1 }
                }
              },
              "titles": {
                "t1": { "@type": "Title", "name": "Researcher", "kind": "title", "organizationId": "org" }
              },
              "emails": {
                "e1": { "@type": "EmailAddress", "address": "person@example.com", "pref": 1 }
              },
              "onlineServices": {
                "o1": { "@type": "OnlineService", "service": "Mastodon", "user": "@person", "uri": "https://social.example/@person" }
              },
              "phones": {
                "p1": { "@type": "Phone", "number": "tel:+38515551234", "features": { "voice": true, "mobile": true } }
              },
              "preferredLanguages": {
                "l1": { "@type": "LanguagePref", "language": "hr-Latn-HR", "contexts": { "work": true }, "pref": 1 }
              },
              "calendars": {
                "c1": { "@type": "Calendar", "kind": "freeBusy", "uri": "https://calendar.example/busy.ics", "mediaType": "text/calendar" }
              },
              "schedulingAddresses": {
                "s1": { "@type": "SchedulingAddress", "uri": "mailto:person@example.com" }
              },
              "addresses": {
                "a1": {
                  "@type": "Address",
                  "components": [
                    { "@type": "AddressComponent", "kind": "number", "value": "1" },
                    { "kind": "name", "value": "Main Street", "phonetic": "meɪn" }
                  ],
                  "countryCode": "HR",
                  "timeZone": "Europe/Zagreb",
                  "contexts": { "delivery": true },
                  "phoneticSystem": "ipa"
                }
              },
              "cryptoKeys": {
                "k1": { "@type": "CryptoKey", "uri": "data:application/pgp-keys;base64,QQ==" }
              },
              "directories": {
                "d1": { "@type": "Directory", "kind": "entry", "uri": "https://directory.example/person.vcf", "listAs": 1 }
              },
              "links": {
                "link": { "@type": "Link", "kind": "contact", "uri": "https://example.com/contact" }
              },
              "media": {
                "photo": { "@type": "Media", "kind": "photo", "uri": "data:image/png;base64,iVBORw0KGgo=", "mediaType": "image/png" }
              },
              "anniversaries": {
                "birth": { "@type": "Anniversary", "kind": "birth", "date": { "@type": "PartialDate", "year": 1980, "month": 2, "day": 29 } },
                "event": { "kind": "example.com:launch", "date": { "@type": "Timestamp", "utc": "2024-01-02T03:04:05Z" } }
              },
              "keywords": { "standards": true },
              "notes": {
                "n1": { "@type": "Note", "note": "Standards contact", "author": { "@type": "Author", "name": "Editor", "uri": "https://example.com/editor" } }
              },
              "personalInfo": {
                "pi": { "@type": "PersonalInfo", "kind": "expertise", "value": "JSContact", "level": "high", "listAs": 1 }
              },
              "futureProperty": { "opaque": true },
              "example.com:opaque": { "@TYPE": false, "arbitrary/key": 7 }
            }
            """)!.AsObject();

        Assert.IsTrue(JmapContactCodec.TryValidate(card, out var invalid),
            string.Join(", ", invalid));
    }

    [TestMethod]
    public void Rfc9553LanguagePrefAndLocalizationPatchVectorIsAccepted()
    {
        var card = MinimalCard();
        card["language"] = "zh-Hant";
        card["preferredLanguages"] = new JsonObject
        {
            ["l1"] = new JsonObject
            {
                ["@type"] = "LanguagePref",
                ["language"] = "en-GB",
                ["pref"] = 1,
            },
        };
        card["name"] = new JsonObject
        {
            ["components"] = new JsonArray(
                new JsonObject { ["kind"] = "surname", ["value"] = "孫" },
                new JsonObject { ["kind"] = "given", ["value"] = "中山" }),
        };
        card["localizations"] = new JsonObject
        {
            ["yue"] = new JsonObject
            {
                ["name/phoneticSystem"] = "jyut",
                ["name/phoneticScript"] = "Latn",
                ["name/components/0/phonetic"] = "syun1",
                ["name/components/1/phonetic"] = "zung1saan1",
            },
        };

        Assert.IsTrue(JmapContactCodec.TryValidate(card, out var invalid),
            string.Join(", ", invalid));
    }

    [TestMethod]
    public void Rfc9553RegisteredNamesTypesEnumsAndDependenciesRejectInvalidValues()
    {
        AssertInvalid("name", card => card["name"] = new JsonObject
        {
            ["Full"] = "wrong case",
        });
        AssertInvalid("name", card => card["name"] = new JsonObject
        {
            ["full"] = "Known property in the wrong object",
            ["address"] = "person@example.com",
        });
        AssertInvalid("name", card => card["name"] = new JsonObject
        {
            ["components"] = new JsonArray(
                new JsonObject { ["kind"] = "prefix", ["value"] = "Dr." }),
        });
        AssertInvalid("name", card => card["name"] = new JsonObject
        {
            ["components"] = new JsonArray(
                new JsonObject { ["kind"] = "given", ["value"] = "John", ["phonetic"] = "dʒɒn" }),
        });
        AssertInvalid("kind", card => card["kind"] = "Individual");
        AssertInvalid("kind", card => card["kind"] = "example.com:bad/value");
        AssertInvalid("language", card => card["language"] = "en_US");
        AssertInvalid("preferredLanguages", card => card["preferredLanguages"] = new JsonObject
        {
            ["l1"] = new JsonObject
            {
                ["@type"] = "LanguagePreference",
                ["language"] = "en",
            },
        });
        AssertInvalid("phones", card => card["phones"] = new JsonObject
        {
            ["p1"] = new JsonObject
            {
                ["number"] = "+1 555 0100",
                ["features"] = new JsonObject { ["Voice"] = true },
            },
        });
        AssertInvalid("organizations", card => card["organizations"] = new JsonObject
        {
            ["o1"] = new JsonObject
            {
                ["name"] = "Example",
                ["units"] = new JsonArray("Research"),
            },
        });
        AssertInvalid("notes", card => card["notes"] = new JsonObject
        {
            ["n1"] = new JsonObject
            {
                ["note"] = "test",
                ["author"] = new JsonObject { ["uri"] = "not a URI" },
            },
        });
        AssertInvalid("created", card => card["created"] = "2024-01-02T03:04:05.100Z");
        AssertInvalid("media", card => card["media"] = new JsonObject
        {
            ["m1"] = new JsonObject
            {
                ["kind"] = "photo",
                ["uri"] = "data:image/png;base64,QQ==",
                ["blobId"] = "U123",
            },
        });
        AssertInvalid("addresses", card => card["addresses"] = new JsonObject
        {
            ["a1"] = new JsonObject
            {
                ["full"] = "Somewhere",
                ["extra"] = true,
            },
        });

        var vendorKind = MinimalCard();
        vendorKind["kind"] = "example.com:robot";
        Assert.IsTrue(JmapContactCodec.TryValidate(vendorKind, out var invalid),
            string.Join(", ", invalid));
    }

    [TestMethod]
    public void Rfc9553LocalizationPatchObjectsRejectInvalidPathsAndResults()
    {
        AssertInvalidLocalization(new JsonObject
        {
            ["en_US"] = new JsonObject { ["name/full"] = "Localized" },
        });
        AssertInvalidLocalization(new JsonObject
        {
            ["en"] = new JsonObject { ["localizations/en/name"] = "recursive" },
        });
        AssertInvalidLocalization(new JsonObject
        {
            ["en"] = new JsonObject
            {
                ["name"] = new JsonObject { ["full"] = "Localized" },
                ["name/full"] = "conflict",
            },
        });
        AssertInvalidLocalization(new JsonObject
        {
            ["en"] = new JsonObject { ["name/missing/value"] = "no parent" },
        });
        AssertInvalidLocalization(new JsonObject
        {
            ["en"] = new JsonObject { ["name/components/0"] = null },
        });
        AssertInvalidLocalization(new JsonObject
        {
            ["en"] = new JsonObject { ["uid"] = null },
        });
        AssertInvalidLocalization(new JsonObject
        {
            ["en"] = new JsonObject { ["name/components/0/kind"] = "Given" },
        });
    }

    private static void AssertInvalid(string property, Action<JsonObject> mutate)
    {
        var card = MinimalCard();
        mutate(card);
        Assert.IsFalse(JmapContactCodec.TryValidate(card, out var invalid));
        CollectionAssert.Contains(invalid.ToArray(), property);
    }

    private static void AssertInvalidLocalization(JsonObject localizations)
    {
        var card = MinimalCard();
        card["name"] = new JsonObject
        {
            ["full"] = "Base",
            ["components"] = new JsonArray(
                new JsonObject { ["kind"] = "given", ["value"] = "Base" }),
        };
        card["localizations"] = localizations;
        Assert.IsFalse(JmapContactCodec.TryValidate(card, out var invalid));
        CollectionAssert.Contains(invalid.ToArray(), "localizations");
    }

    private static JsonObject MinimalCard() => new()
    {
        ["@type"] = "Card",
        ["version"] = "1.0",
        ["uid"] = "validator-vector",
    };
}
