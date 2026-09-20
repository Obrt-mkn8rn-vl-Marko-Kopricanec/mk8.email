using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapProtocolTests
{
    private const string Core = "urn:ietf:params:jmap:core";
    private const string Mail = "urn:ietf:params:jmap:mail";
    private const string Submission = "urn:ietf:params:jmap:submission";
    private const string Vacation = "urn:ietf:params:jmap:vacationresponse";

    [TestMethod]
    public async Task MailboxLifecycleProducesChangesAndEnforcesRegisteredRoles()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var oldState = await GetStateAsync(fixture, JmapConstants.MailboxDataType);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {
              "child": {"name":"2026", "parentId":"#parent"},
              "parent": {"name":"Projects", "role":"archive", "sortOrder":20}
            }
          }, "c1"]]
        }
        """);
        var created = Arguments(create)["created"]!.AsObject();
        var parentId = created["parent"]!["id"]!.GetValue<string>();
        var childId = created["child"]!["id"]!.GetValue<string>();
        Assert.AreEqual(parentId, created["child"]!["parentId"]!.GetValue<string>());

        var query = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/query", {
            "accountId": "{{{fixture.AccountId}}}",
            "filter": {"name":"Project"},
            "sortAsTree": true,
            "calculateTotal": true
          }, "q1"]]
        }
        """);
        Assert.AreEqual(parentId, Arguments(query)["ids"]![0]!.GetValue<string>());
        Assert.AreEqual(1, Arguments(query)["total"]!.GetValue<int>());

        var changes = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/changes", {
            "accountId": "{{{fixture.AccountId}}}", "sinceState":"{{{oldState}}}"
          }, "ch1"]]
        }
        """);
        var createdIds = Arguments(changes)["created"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(createdIds.SetEquals([parentId, childId]));

        var invalidRole = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {"bad":{"name":"Bad role", "role":"made-up"}}
          }, "m2"]]
        }
        """);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(invalidRole)["notCreated"]!["bad"]!["type"]!.GetValue<string>());

        var protectedParent = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}", "destroy":["{{{parentId}}}"]
          }, "m3"]]
        }
        """);
        Assert.AreEqual(
            "mailboxHasChild",
            Arguments(protectedParent)["notDestroyed"]![parentId]!["type"]!.GetValue<string>());

        var destroy = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}", "destroy":["{{{parentId}}}", "{{{childId}}}"]
          }, "m4"]]
        }
        """);
        CollectionAssert.AreEqual(
            new[] { childId, parentId },
            Arguments(destroy)["destroyed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task MailboxNamesMustUseNetUnicode()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {
              "bom": {"name":"\uFEFFHidden"},
              "unassigned": {"name":"Unassigned \u0378"},
              "normalized": {"name":"Cafe\u0301"}
            }
          }, "m1"]]
        }
        """);

        var notCreated = Arguments(response)["notCreated"]!.AsObject();
        Assert.AreEqual("invalidProperties", notCreated["bom"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", notCreated["unassigned"]!["type"]!.GetValue<string>());
        var mailboxId = Arguments(response)["created"]!["normalized"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}", "ids":["{{{mailboxId}}}"],
            "properties":["name"]
          }, "g1"]]
        }
        """);
        Assert.AreEqual("Caf\u00e9", Arguments(get)["list"]![0]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MailboxSetUsesFinalStateForSwapsAndAcceptsGetObjects()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {
              "alpha": {"name":"Alpha", "role":"flagged", "sortOrder":10},
              "beta": {"name":"Beta", "role":"important", "sortOrder":20}
            }
          }, "m1"]]
        }
        """);
        var created = Arguments(create)["created"]!.AsObject();
        var alphaId = created["alpha"]!["id"]!.GetValue<string>();
        var betaId = created["beta"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}",
            "ids": ["{{{alphaId}}}", "{{{betaId}}}"]
          }, "g1"]]
        }
        """);
        var mailboxes = Arguments(get)["list"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(mailbox => mailbox["id"]!.GetValue<string>(), StringComparer.Ordinal);

        var alphaRoundTrip = (JsonObject)mailboxes[alphaId].DeepClone();
        alphaRoundTrip["sortOrder"] = 11;
        var roundTripUpdate = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Mail),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "Mailbox/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["update"] = new JsonObject { [alphaId] = alphaRoundTrip },
                },
                "m2")),
        });
        Assert.IsNull(Arguments(roundTripUpdate)["notUpdated"]);
        Assert.IsTrue(Arguments(roundTripUpdate)["updated"]!.AsObject().ContainsKey(alphaId));

        var alphaSwap = (JsonObject)mailboxes[alphaId].DeepClone();
        alphaSwap["name"] = "Beta";
        alphaSwap["role"] = "important";
        alphaSwap["sortOrder"] = 11;
        var betaSwap = (JsonObject)mailboxes[betaId].DeepClone();
        betaSwap["name"] = "Alpha";
        betaSwap["role"] = "flagged";
        var swap = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Mail),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "Mailbox/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["update"] = new JsonObject
                    {
                        [alphaId] = alphaSwap,
                        [betaId] = betaSwap,
                    },
                },
                "m3")),
        });
        Assert.IsNull(Arguments(swap)["notUpdated"]);
        CollectionAssert.AreEquivalent(
            new[] { alphaId, betaId },
            Arguments(swap)["updated"]!.AsObject().Select(item => item.Key).ToArray());

        var verify = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}",
            "ids": ["{{{alphaId}}}", "{{{betaId}}}"]
          }, "g2"]]
        }
        """);
        var swapped = Arguments(verify)["list"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(mailbox => mailbox["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Beta", swapped[alphaId]["name"]!.GetValue<string>());
        Assert.AreEqual("important", swapped[alphaId]["role"]!.GetValue<string>());
        Assert.AreEqual(11, swapped[alphaId]["sortOrder"]!.GetValue<int>());
        Assert.AreEqual("Alpha", swapped[betaId]["name"]!.GetValue<string>());
        Assert.AreEqual("flagged", swapped[betaId]["role"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MailboxSetUsesFinalStateAcrossCreatesUpdatesAndDestroys()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var seed = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {
              "move": {"name":"Taken", "role":"flagged"},
              "remove": {"name":"Removed", "role":"important"}
            }
          }, "m1"]]
        }
        """);
        var seeded = Arguments(seed)["created"]!.AsObject();
        var moveId = seeded["move"]!["id"]!.GetValue<string>();
        var removeId = seeded["remove"]!["id"]!.GetValue<string>();

        var replace = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {
              "reuseUpdate": {"name":"Taken", "role":"flagged"},
              "reuseDestroy": {"name":"Removed", "role":"important"}
            },
            "update": {
              "{{{moveId}}}": {"name":"Moved", "role":"archive"}
            },
            "destroy": ["{{{removeId}}}"]
          }, "m2"]]
        }
        """);
        var arguments = Arguments(replace);
        Assert.IsNull(arguments["notCreated"]);
        Assert.IsNull(arguments["notUpdated"]);
        Assert.IsNull(arguments["notDestroyed"]);
        Assert.IsTrue(arguments["updated"]!.AsObject().ContainsKey(moveId));
        CollectionAssert.AreEqual(
            new[] { removeId },
            arguments["destroyed"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());

        var replacements = arguments["created"]!.AsObject();
        var reusedUpdateId = replacements["reuseUpdate"]!["id"]!.GetValue<string>();
        var reusedDestroyId = replacements["reuseDestroy"]!["id"]!.GetValue<string>();
        var verify = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}",
            "ids": ["{{{moveId}}}", "{{{removeId}}}", "{{{reusedUpdateId}}}", "{{{reusedDestroyId}}}"]
          }, "g1"]]
        }
        """);
        var byId = Arguments(verify)["list"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(mailbox => mailbox["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Moved", byId[moveId]["name"]!.GetValue<string>());
        Assert.AreEqual("archive", byId[moveId]["role"]!.GetValue<string>());
        Assert.AreEqual("Taken", byId[reusedUpdateId]["name"]!.GetValue<string>());
        Assert.AreEqual("flagged", byId[reusedUpdateId]["role"]!.GetValue<string>());
        Assert.AreEqual("Removed", byId[reusedDestroyId]["name"]!.GetValue<string>());
        Assert.AreEqual("important", byId[reusedDestroyId]["role"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { removeId },
            Arguments(verify)["notFound"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());

        var secondSeed = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {
              "shift": {"name":"Shift", "role":"memos"},
              "vacate": {"name":"Vacate", "role":"snoozed"}
            }
          }, "m3"]]
        }
        """);
        var secondSeeded = Arguments(secondSeed)["created"]!.AsObject();
        var shiftId = secondSeeded["shift"]!["id"]!.GetValue<string>();
        var vacateId = secondSeeded["vacate"]!["id"]!.GetValue<string>();
        var updateIntoDestroyedValues = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "update": {
              "{{{shiftId}}}": {"name":"Vacate", "role":"snoozed"}
            },
            "destroy": ["{{{vacateId}}}"]
          }, "m4"]]
        }
        """);
        Assert.IsNull(Arguments(updateIntoDestroyedValues)["notUpdated"]);
        Assert.IsNull(Arguments(updateIntoDestroyedValues)["notDestroyed"]);
        Assert.IsTrue(Arguments(updateIntoDestroyedValues)["updated"]!
            .AsObject().ContainsKey(shiftId));
        CollectionAssert.AreEqual(
            new[] { vacateId },
            Arguments(updateIntoDestroyedValues)["destroyed"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task ImportedEmailPreservesExactRawOctets()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.Latin1.GetBytes(
            $"From: sender@example.net\nTo: {fixture.User.Username}\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\n"
            + "Subject: Exact raw bytes\n\nbody with LF only\n");
        var uploadBlobId = await fixture.StoreBlobAsync(raw);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"raw":{"blobId":"{{{uploadBlobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["raw"]!["id"]!.GetValue<string>();
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["id", "blobId", "size"]
          }, "g1"]]
        }
        """);
        var email = Arguments(get)["list"]![0]!;
        Assert.AreEqual(raw.LongLength, email["size"]!.GetValue<long>());

        using var scope = fixture.Services.CreateScope();
        var storedBlob = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(
                fixture.InboxId,
                email["blobId"]!.GetValue<string>(),
                CancellationToken.None);
        Assert.IsNotNull(storedBlob);
        CollectionAssert.AreEqual(raw, storedBlob.Content);
        Assert.IsTrue(JmapId.TryParseEmail(emailId, out var storedEmailId));
        var storedEmail = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .Emails.AsNoTracking().SingleAsync(message => message.Id == storedEmailId);
        Assert.IsNotNull(storedEmail.RawMessage);
        CollectionAssert.AreEqual(raw, storedEmail.RawMessage);
    }

    [TestMethod]
    public async Task EmailWritesRejectNullValuesForNonNullableDefaults()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var blobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n\r\nbody"));

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/set", {
              "accountId":"{{{fixture.AccountId}}}",
              "create":{
                "nullKeywords":{
                  "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                  "keywords":null,
                  "bodyValues":{"1":{"value":"body"}},
                  "textBody":[{"partId":"1", "type":"text/plain"}]
                },
                "nullReceivedAt":{
                  "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                  "receivedAt":null,
                  "bodyValues":{"1":{"value":"body"}},
                  "textBody":[{"partId":"1", "type":"text/plain"}]
                },
                "defaults":{
                  "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                  "bodyValues":{"1":{"value":"body"}},
                  "textBody":[{"partId":"1", "type":"text/plain"}]
                }
              }
            }, "s1"],
            ["Email/import", {
              "accountId":"{{{fixture.AccountId}}}",
              "emails":{
                "nullKeywords":{
                  "blobId":"{{{blobId}}}",
                  "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                  "keywords":null
                },
                "nullReceivedAt":{
                  "blobId":"{{{blobId}}}",
                  "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                  "receivedAt":null
                },
                "defaults":{
                  "blobId":"{{{blobId}}}",
                  "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true}
                }
              }
            }, "i1"]
          ]
        }
        """);

        foreach (var index in new[] { 0, 1 })
        {
            var arguments = Arguments(response, index);
            Assert.AreEqual(
                "invalidProperties",
                arguments["notCreated"]!["nullKeywords"]!["type"]!.GetValue<string>());
            Assert.AreEqual(
                "invalidProperties",
                arguments["notCreated"]!["nullReceivedAt"]!["type"]!.GetValue<string>());
            Assert.IsNotNull(arguments["created"]!["defaults"]);
        }
    }

    [TestMethod]
    public async Task EmailReadsFailInsteadOfHidingCorruptStoredRecords()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var emailId = Guid.CreateVersion7();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            database.Emails.Add(new EmailDB
            {
                Id = emailId,
                Sender = "sender@example.net",
                Recipient = fixture.User.Username,
                Subject = "Corrupt raw message",
                Body = string.Empty,
                RawMessage = [],
                SizeBytes = 0,
                ReceivedAt = DateTime.UtcNow,
                FolderId = fixture.InboxFolderId,
                Uid = 100,
            });
            await database.SaveChangesAsync();
        }

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/get", {
              "accountId": "{{{fixture.AccountId}}}",
              "ids": ["{{{JmapId.Email(emailId)}}}"]
            }, "g1"],
            ["Email/query", {
              "accountId": "{{{fixture.AccountId}}}",
              "calculateTotal": true
            }, "q1"]
          ]
        }
        """);

        Assert.AreEqual("error", response["methodResponses"]![0]![0]!.GetValue<string>());
        Assert.AreEqual("serverFail", Arguments(response)["type"]!.GetValue<string>());
        Assert.AreEqual("error", response["methodResponses"]![1]![0]!.GetValue<string>());
        Assert.AreEqual("serverFail", Arguments(response, 1)["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EmailBodyValuesReportMalformedCharsetData()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var prefix = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Invalid UTF-8\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: 8bit\r\n\r\n"
            + "before ");
        var suffix = Encoding.ASCII.GetBytes(" after\r\n");
        var raw = prefix.Concat(new byte[] { 0xc3, 0x28 }).Concat(suffix).ToArray();
        var blobId = await fixture.StoreBlobAsync(raw);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["textBody", "bodyValues"],
            "fetchTextBodyValues": true
          }, "p1"]]
        }
        """);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        var partId = parsed["textBody"]![0]!["partId"]!.GetValue<string>();
        var bodyValue = parsed["bodyValues"]![partId]!;
        Assert.IsTrue(bodyValue["isEncodingProblem"]!.GetValue<bool>());
        StringAssert.Contains(bodyValue["value"]!.GetValue<string>(), "\ufffd(");
    }

    [TestMethod]
    public async Task HtmlBodyValueTruncationStopsBeforeAnOpenTag()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/html; charset=us-ascii\r\n\r\n"
            + "<p>Hello</p><a title=\"1 > 0\" href=\"https://example.com\">world</a>");
        var blobId = await fixture.StoreBlobAsync(raw);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["htmlBody", "bodyValues"],
            "fetchHTMLBodyValues": true,
            "maxBodyValueBytes": 30
          }, "p1"]]
        }
        """);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        var partId = parsed["htmlBody"]![0]!["partId"]!.GetValue<string>();
        var bodyValue = parsed["bodyValues"]![partId]!;
        var value = bodyValue["value"]!.GetValue<string>();
        Assert.AreEqual("<p>Hello</p>", value);
        Assert.IsTrue(bodyValue["isTruncated"]!.GetValue<bool>());
        Assert.IsTrue(Encoding.UTF8.GetByteCount(value) <= 30);
    }

    [TestMethod]
    public async Task EmailParsedHeaderTextDropsDecodedControlsAndNormalizesUnicode()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        const string decodedSubject = "Clean\0\u0001 Cafe\u0301";
        const string decodedName = "Sender Cafe\u0301";
        var encodedSubject = Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedSubject));
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedName));
        var raw = Encoding.ASCII.GetBytes(
            $"From: =?utf-8?B?{encodedName}?= <sender@example.net>\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + $"Subject: =?utf-8?B?{encodedSubject}?=\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["subject", "header:Subject:asText", "from"]
          }, "p1"]]
        }
        """);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        var expectedSubject = "Clean Cafe\u0301".Normalize(NormalizationForm.FormC);
        var expectedName = decodedName.Normalize(NormalizationForm.FormC);
        Assert.AreEqual(expectedSubject, parsed["subject"]!.GetValue<string>());
        Assert.AreEqual(
            expectedSubject,
            parsed["header:Subject:asText"]!.GetValue<string>());
        Assert.AreEqual(
            expectedName,
            parsed["from"]![0]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EmailParsedMessageIdsUseRfc5322Syntax()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Message-ID: <not a message id>\r\n"
            + "References: (first) <valid@example.test>\r\n"
            + " <\"quoted local\"@example.test>\r\n"
            + "Resent-Message-ID: <valid@example.test> invalid\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": [
              "messageId",
              "references",
              "header:Resent-Message-ID:asMessageIds"
            ]
          }, "p1"]]
        }
        """);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.IsNull(parsed["messageId"]);
        CollectionAssert.AreEqual(
            new[] { "valid@example.test", "\"quoted local\"@example.test" },
            parsed["references"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        Assert.IsNull(parsed["header:Resent-Message-ID:asMessageIds"]);
    }

    [TestMethod]
    public async Task EmailParsedUrlsRejectInvalidBracketedValues()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "List-Unsubscribe: <not a url>\r\n"
            + "List-Help: (preferred) <https://example.test/help>, <mailto:help@example.test>\r\n"
            + "List-Subscribe: <https://example.test/sub scribe>\r\n"
            + "List-Owner: invalid <mailto:owner@example.test>\r\n"
            + "List-Archive: <https://example.test/first>, invalid, <https://example.test/last>\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": [
              "header:List-Unsubscribe:asURLs",
              "header:List-Help:asURLs",
              "header:List-Subscribe:asURLs",
              "header:List-Owner:asURLs",
              "header:List-Archive:asURLs"
            ]
          }, "p1"]]
        }
        """);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.IsNull(parsed["header:List-Unsubscribe:asURLs"]);
        CollectionAssert.AreEqual(
            new[] { "https://example.test/help", "mailto:help@example.test" },
            parsed["header:List-Help:asURLs"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "https://example.test/subscribe" },
            parsed["header:List-Subscribe:asURLs"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        Assert.IsNull(parsed["header:List-Owner:asURLs"]);
        CollectionAssert.AreEqual(
            new[] { "https://example.test/first" },
            parsed["header:List-Archive:asURLs"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task EmailParsedDatesUseRfc5322Syntax()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Date: 2026-01-02T03:04:05Z\r\n"
            + "Resent-Date: Fri, 2 Jan 2026 03:04:05 +0000\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["sentAt", "header:Resent-Date:asDate"]
          }, "p1"]]
        }
        """);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.IsNull(parsed["sentAt"]);
        Assert.AreEqual(
            "2026-01-02T03:04:05Z",
            parsed["header:Resent-Date:asDate"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DeletingLegacyEmailWithNullThreadDestroysItsFallbackThread()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var create = await CreateTextEmailAsync(
            fixture,
            "legacyThread",
            "Legacy thread",
            "body");
        var emailId = Arguments(create)["created"]!["legacyThread"]!["id"]!
            .GetValue<string>();
        Assert.IsTrue(JmapId.TryParseEmail(emailId, out var databaseId));
        var fallbackThreadId = JmapId.Thread(databaseId.ToString("N"));

        string beforeDelete;
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var email = await database.Emails.SingleAsync(item => item.Id == databaseId);
            email.ThreadObjectId = null;
            await database.SaveChangesAsync();
            beforeDelete = await scope.ServiceProvider.GetRequiredService<JmapStateService>()
                .GetStateAsync(fixture.InboxId, JmapConstants.ThreadDataType);

            database.Emails.Remove(email);
            await database.SaveChangesAsync();
        }

        var changes = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Thread/changes",{
            "accountId":"{{{fixture.AccountId}}}",
            "sinceState":"{{{beforeDelete}}}"
          },"c1"]]
        }
        """);
        CollectionAssert.AreEqual(
            new[] { fallbackThreadId },
            Arguments(changes)["destroyed"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task EmailLifecycleProjectsMimeAndPreservesImapState()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var oldState = await GetStateAsync(fixture, JmapConstants.EmailDataType);
        var create = await CreateTextEmailAsync(fixture, "draft1", "JMAP lifecycle", "Hello from JMAP");
        var emailId = Arguments(create)["created"]!["draft1"]!["id"]!.GetValue<string>();
        var threadId = Arguments(create)["created"]!["draft1"]!["threadId"]!.GetValue<string>();

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var stored = await database.Emails.Include(email => email.Folder).SingleAsync();
            Assert.AreEqual(1, stored.Uid);
            Assert.IsTrue(stored.ModSeq > 0);
            Assert.AreEqual(fixture.InboxFolderId, stored.FolderId);
            StringAssert.Contains(stored.RawHeaders!, "Subject: JMAP lifecycle");
        }

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["id", "threadId", "mailboxIds", "keywords", "subject",
              "header:Subject:asText", "bodyStructure", "bodyValues", "textBody", "preview"],
            "bodyProperties":["partId", "type", "charset"],
            "fetchTextBodyValues":true,
            "maxBodyValueBytes":5
          }, "g1"]]
        }
        """);
        var email = Arguments(get)["list"]![0]!.AsObject();
        Assert.AreEqual("JMAP lifecycle", email["subject"]!.GetValue<string>());
        Assert.AreEqual("JMAP lifecycle", email["header:Subject:asText"]!.GetValue<string>());
        Assert.AreEqual("Hello", email["bodyValues"]!["1"]!["value"]!.GetValue<string>());
        Assert.IsTrue(email["bodyValues"]!["1"]!["isTruncated"]!.GetValue<bool>());
        Assert.IsTrue(email["keywords"]!["$draft"]!.GetValue<bool>());

        var query = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/query", {
            "accountId":"{{{fixture.AccountId}}}",
            "filter":{"subject":"lifecycle", "hasKeyword":"$draft"},
            "sort":[{"property":"receivedAt", "isAscending":false}],
            "calculateTotal":true
          }, "q1"]]
        }
        """);
        Assert.AreEqual(emailId, Arguments(query)["ids"]![0]!.GetValue<string>());
        Assert.AreEqual(1, Arguments(query)["total"]!.GetValue<int>());

        var updateObject = (JsonObject)email.DeepClone();
        updateObject["mailboxIds"] = new JsonObject { [fixture.DraftsMailboxId] = true };
        updateObject["keywords"]!["$seen"] = true;
        var update = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Mail),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "Email/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["update"] = new JsonObject { [emailId] = updateObject },
                },
                "s2")),
        });
        Assert.IsTrue(Arguments(update)["updated"]!.AsObject().ContainsKey(emailId));
        var updatedState = Arguments(update)["newState"]!.GetValue<string>();

        var changedSubject = (JsonObject)updateObject.DeepClone();
        changedSubject["subject"] = "Changed immutable subject";
        var invalidUpdate = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Mail),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "Email/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["update"] = new JsonObject { [emailId] = changedSubject },
                },
                "s2b")),
        });
        Assert.AreEqual(
            "invalidProperties",
            Arguments(invalidUpdate)["notUpdated"]![emailId]!["type"]!.GetValue<string>());

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var stored = await database.Emails.SingleAsync();
            Assert.AreEqual(fixture.DraftsFolderId, stored.FolderId);
            Assert.AreEqual(1, stored.Uid);
            Assert.IsTrue(stored.IsRead);
            Assert.AreEqual(1, await database.ExpungedUids.CountAsync());
        }

        var changes = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/changes", {
            "accountId":"{{{fixture.AccountId}}}", "sinceState":"{{{oldState}}}"
          }, "ch1"], ["Thread/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{threadId}}}"]
          }, "t1"]]
        }
        """);
        CollectionAssert.Contains(
            Arguments(changes)["created"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray(),
            emailId);
        Assert.AreEqual(emailId, Arguments(changes, 1)["list"]![0]!["emailIds"]![0]!.GetValue<string>());

        var destroy = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}", "destroy":["{{{emailId}}}"]
          }, "s3"], ["Email/changes", {
            "accountId":"{{{fixture.AccountId}}}", "sinceState":"{{{updatedState}}}"
          }, "ch2"]]
        }
        """);
        Assert.AreEqual(emailId, Arguments(destroy)["destroyed"]![0]!.GetValue<string>());
        Assert.AreEqual(emailId, Arguments(destroy, 1)["destroyed"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EmailCreationRejectsAmbiguousHeadersAndInvalidBodyParts()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var existingBlob = await fixture.StoreBlobAsync([1, 2, 3], "application/octet-stream");
        var missingOne = JmapId.UploadedBlob(Guid.CreateVersion7());
        var missingTwo = JmapId.UploadedBlob(Guid.CreateVersion7());
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create": {
              "wrongForm": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "header:From:asDate":"2026-01-01T00:00:00Z",
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "duplicateRoot": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "subject":"top",
                "bodyValues":{"1":{"value":"body"}},
                "bodyStructure":{"partId":"1", "type":"text/plain", "header:Subject":" root"}
              },
              "partSize": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "size":4, "type":"text/plain"}]
              },
              "partCharsetNull": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "charset":null, "type":"text/plain"}]
              },
              "emptyTextBody": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "textBody":[]
              },
              "nullTextBody": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "textBody":null
              },
              "invalidBodyValue": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body", "isTruncated":true}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "nonTextCharset": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "attachments":[{
                  "blobId":"{{{existingBlob}}}", "type":"application/octet-stream", "charset":"utf-8"
                }]
              },
              "invalidBlobSize": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "attachments":[{
                  "blobId":"{{{existingBlob}}}", "type":"application/octet-stream", "size":-1
                }]
              },
              "invalidMessageId": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "messageId":["not a message id"],
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "invalidParsedHeader": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "header:X-Tracking:asMessageIds":["<already-wrapped@example.test>"],
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "missingGroupedAddresses": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "header:X-Team:asGroupedAddresses":[{"name":"Team"}],
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "nullGroupedAddresses": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "header:X-Team:asGroupedAddresses":[{"name":"Team", "addresses":null}],
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "duplicateNullPartHeader": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "bodyStructure":{
                  "partId":"1", "type":"text/plain", "cid":null,
                  "header:Content-ID":" <part@example.test>"
                }
              },
              "allMissingBlobs": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "attachments":[
                  {"blobId":"{{{missingOne}}}", "type":"application/octet-stream"},
                  {"blobId":"{{{missingTwo}}}", "type":"application/octet-stream"}
                ]
              },
              "nullableMetadata": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "messageId":null,
                "sender":null,
                "bodyValues":{"1":{"value":"body"}},
                "bodyStructure":{
                  "partId":"1", "type":"text/plain", "language":null, "subParts":null
                }
              }
            }
          }, "s1"]]
        }
        """);
        var failures = Arguments(response)["notCreated"]!.AsObject();
        Assert.AreEqual("invalidProperties", failures["wrongForm"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["duplicateRoot"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["partSize"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["partCharsetNull"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["emptyTextBody"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["nullTextBody"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["invalidBodyValue"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["nonTextCharset"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["invalidBlobSize"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["invalidMessageId"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["invalidParsedHeader"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["missingGroupedAddresses"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["nullGroupedAddresses"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["duplicateNullPartHeader"]!["type"]!.GetValue<string>());
        Assert.AreEqual("blobNotFound", failures["allMissingBlobs"]!["type"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(
            new[] { missingOne, missingTwo },
            failures["allMissingBlobs"]!["notFound"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.IsNotNull(Arguments(response)["created"]!["nullableMetadata"]);
    }

    [TestMethod]
    public async Task EmailCreationPreservesMultipleInReplyToMessageIds()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"reply":{
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
              "inReplyTo":["first@example.test", "second@example.test"],
              "bodyValues":{"1":{"value":"body"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "s1"]]
        }
        """);
        var emailId = Arguments(create)["created"]!["reply"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "ids":["{{{emailId}}}"],
            "properties":["inReplyTo", "header:In-Reply-To:asMessageIds"]
          }, "g1"]]
        }
        """);
        var email = Arguments(get)["list"]![0]!;
        var expected = new[] { "first@example.test", "second@example.test" };
        CollectionAssert.AreEqual(
            expected,
            email["inReplyTo"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            expected,
            email["header:In-Reply-To:asMessageIds"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task MimeHeadersSupportPermittedParsedForms()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"mime":{
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
              "bodyValues":{"1":{"value":"body"}},
              "bodyStructure":{
                "partId":"1",
                "type":"text/plain",
                "header:Content-ID:asMessageIds":["part@example.test"]
              }
            }}
          }, "s1"]]
        }
        """);
        var emailId = Arguments(create)["created"]!["mime"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "ids":["{{{emailId}}}"],
            "properties":["headers", "header:Content-Type:asText", "bodyStructure"],
            "bodyProperties":["cid", "header:Content-ID:asMessageIds"]
          }, "g1"]]
        }
        """);
        var email = Arguments(get)["list"]![0]!;
        StringAssert.StartsWith(
            email["header:Content-Type:asText"]!.GetValue<string>(),
            "text/plain");
        var headerNames = email["headers"]!.AsArray()
            .Select(header => header!["name"]!.GetValue<string>())
            .ToArray();
        CollectionAssert.Contains(headerNames, "Content-Type");
        CollectionAssert.Contains(headerNames, "Content-ID");
        Assert.AreEqual("part@example.test", email["bodyStructure"]!["cid"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "part@example.test" },
            email["bodyStructure"]!["header:Content-ID:asMessageIds"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());

        var query = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/query", {
            "accountId":"{{{fixture.AccountId}}}",
            "filter":{"header":["Content-Type", "text/plain"]}
          }, "q1"]]
        }
        """);
        CollectionAssert.AreEqual(
            new[] { emailId },
            Arguments(query)["ids"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task UploadBlobCanBeParsedAndImported()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.UTF8.GetBytes(
            "From: Sender <sender@example.net>\r\n"
            + "To: user@mk8n.com\r\n"
            + "Subject: Imported message\r\n"
            + "Message-ID: <import-1@example.net>\r\n"
            + "Date: Sat, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\nImported body");
        var blobId = await fixture.StoreBlobAsync(raw);

        var parse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}", "blobIds":["{{{blobId}}}"],
            "properties":["subject", "from", "bodyValues"],
            "fetchTextBodyValues":true
          }, "p1"], ["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"imp":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
              "keywords":{"$seen":true} } }
          }, "i1"]]
        }
        """);
        Assert.AreEqual(
            "Imported message",
            Arguments(parse)["parsed"]![blobId]!["subject"]!.GetValue<string>());
        Assert.AreEqual(
            "Imported body",
            Arguments(parse)["parsed"]![blobId]!["bodyValues"]!["1"]!["value"]!.GetValue<string>());
        var importedId = Arguments(parse, 1)["created"]!["imp"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{importedId}}}"],
            "properties":["id", "subject", "keywords"]
          }, "g1"]]
        }
        """);
        Assert.AreEqual("Imported message", Arguments(get)["list"]![0]!["subject"]!.GetValue<string>());
        Assert.IsTrue(Arguments(get)["list"]![0]!["keywords"]!["$seen"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ImportDefaultsReceivedAtFromRfcReceivedDates()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.ASCII.GetBytes(
            "Received: from final.example by mx.example; Fri, 2 Jan 2026 03:04:05 EST\r\n"
            + "Received: from origin.example by final.example; Fri, 2 Jan 2026 07:00:00 +0000\r\n"
            + "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw);

        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{
              "received":{
                "blobId":"{{{blobId}}}",
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true}
              }
            }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["received"]!["id"]!.GetValue<string>();
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["receivedAt"]
          }, "g1"]]
        }
        """);
        Assert.AreEqual(
            "2026-01-02T08:04:05Z",
            Arguments(get)["list"]![0]!["receivedAt"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task HtmlSearchAndPreviewIgnoreNonRenderedContent()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Subject: HTML search\r\nContent-Type: text/html; charset=utf-8\r\n\r\n"
            + "<html><head><title>private-head-token</title></head><body>"
            + "<style>.private-style-token { display: none }</style>"
            + "<script>private-script-token</script>"
            + "<p>Visible &amp; searchable</p></body></html>");
        var blobId = await fixture.StoreBlobAsync(raw);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"html":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["html"]!["id"]!.GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"operator":"OR", "conditions":[
                {"body":"private-head-token"},
                {"body":"private-style-token"},
                {"body":"private-script-token"}
              ]},
              "calculateTotal":true
            }, "q1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"Visible & searchable"},
              "calculateTotal":true
            }, "q2"],
            ["Email/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "ids":["{{{emailId}}}"],
              "properties":["preview"]
            }, "g1"],
            ["SearchSnippet/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"searchable"},
              "emailIds":["{{{emailId}}}"]
            }, "ss1"]
          ]
        }
        """);

        Assert.AreEqual(0, Arguments(response)["total"]!.GetValue<int>());
        Assert.AreEqual(1, Arguments(response, 1)["total"]!.GetValue<int>());
        Assert.AreEqual(emailId, Arguments(response, 1)["ids"]![0]!.GetValue<string>());
        Assert.AreEqual(
            "Visible & searchable",
            Arguments(response, 2)["list"]![0]!["preview"]!.GetValue<string>());
        var snippet = Arguments(response, 3)["list"]![0]!["preview"]!.GetValue<string>();
        StringAssert.Contains(snippet, "Visible &amp; <mark>searchable</mark>");
        Assert.IsFalse(snippet.Contains("private-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchIncludesEveryInlineTextBodyPart()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Subject: Multipart search\r\n"
            + "Content-Type: multipart/mixed; boundary=parts\r\n\r\n"
            + "--parts\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nOpening text\r\n"
            + "--parts\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nClosing needle\r\n"
            + "--parts--\r\n");
        var blobId = await fixture.StoreBlobAsync(raw);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"multipart":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["multipart"]!["id"]!.GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"closing needle"}
            }, "q1"],
            ["SearchSnippet/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"closing needle"},
              "emailIds":["{{{emailId}}}"]
            }, "ss1"]
          ]
        }
        """);

        Assert.AreEqual(emailId, Arguments(response)["ids"]![0]!.GetValue<string>());
        StringAssert.Contains(
            Arguments(response, 1)["list"]![0]!["preview"]!.GetValue<string>(),
            "<mark>Closing</mark> <mark>needle</mark>");
    }

    [TestMethod]
    public async Task SearchIncludesTextInsideAttachedMessages()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Subject: Attached message search\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=parts\r\n\r\n"
            + "--parts\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nOuter text\r\n"
            + "--parts\r\nContent-Type: message/rfc822\r\n"
            + "Content-Disposition: attachment; filename=nested.eml\r\n\r\n"
            + "From: nested@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Nested subject\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\n"
            + "Attached searchable needle\r\n"
            + "--parts--\r\n");
        var blobId = await fixture.StoreBlobAsync(raw);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"attached":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["attached"]!["id"]!.GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"attached searchable needle"}
            }, "q1"],
            ["SearchSnippet/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"attached searchable needle"},
              "emailIds":["{{{emailId}}}"]
            }, "ss1"]
          ]
        }
        """);

        Assert.AreEqual(emailId, Arguments(response)["ids"]![0]!.GetValue<string>());
        StringAssert.Contains(
            Arguments(response, 1)["list"]![0]!["preview"]!.GetValue<string>(),
            "<mark>Attached</mark> <mark>searchable</mark> <mark>needle</mark>");
    }

    [TestMethod]
    public async Task SearchSnippetPreservesPlainTextHtmlEntities()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Subject: Plain text entities\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
            + "Literal entity &lt;value&gt;");
        var blobId = await fixture.StoreBlobAsync(raw);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"plain":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["plain"]!["id"]!.GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["SearchSnippet/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "filter":{"body":"Literal"},
            "emailIds":["{{{emailId}}}"]
          }, "ss1"]]
        }
        """);

        Assert.AreEqual(
            "<mark>Literal</mark> entity &amp;lt;value&amp;gt;",
            Arguments(response)["list"]![0]!["preview"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MimeProjectionPreservesNestedStructureAndResolvablePartBlobs()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var relatedRaw = Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\n"
            + "To: user@mk8n.com\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Subject: Nested structure\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=outer\r\n\r\n"
            + "--outer\r\nContent-Type: multipart/alternative; boundary=alternative\r\n\r\n"
            + "--alternative\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello\r\n"
            + "--alternative\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Hello</p>\r\n"
            + "--alternative--\r\n"
            + "--outer\r\nContent-Type: image/png\r\nContent-Disposition: inline\r\n\r\npng\r\n"
            + "--outer--\r\n");
        var relatedBlobId = await fixture.StoreBlobAsync(relatedRaw);
        var previewRaw = Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\nTo: user@mk8n.com\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: 8bit\r\n\r\n"
            + string.Concat(Enumerable.Repeat("😀", 300)));
        var previewBlobId = await fixture.StoreBlobAsync(previewRaw);
        var htmlPreviewBlobId = await fixture.StoreBlobAsync(Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\nTo: user@mk8n.com\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n\r\n"
            + "<p>Hello &amp; welcome</p>"));

        var projection = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{relatedBlobId}}}", "{{{previewBlobId}}}", "{{{htmlPreviewBlobId}}}"],
            "properties":["bodyStructure", "attachments", "hasAttachment", "preview"],
            "bodyProperties":["partId", "blobId", "type", "disposition"]
          }, "p1"]]
        }
        """);
        var related = Arguments(projection)["parsed"]![relatedBlobId]!;
        var rootChildren = related["bodyStructure"]!["subParts"]!.AsArray();
        Assert.AreEqual(2, rootChildren.Count);
        Assert.AreEqual(2, rootChildren[0]!["subParts"]!.AsArray().Count);
        Assert.AreEqual(0, related["attachments"]!.AsArray().Count);
        Assert.IsFalse(related["hasAttachment"]!.GetValue<bool>());
        Assert.AreEqual(
            256,
            Arguments(projection)["parsed"]![previewBlobId]!["preview"]!
                .GetValue<string>()
                .EnumerateRunes()
                .Count());
        Assert.AreEqual(
            "Hello & welcome",
            Arguments(projection)["parsed"]![htmlPreviewBlobId]!["preview"]!
                .GetValue<string>());

        var attachedRaw = Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\nTo: user@mk8n.com\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=outer\r\n\r\n"
            + "--outer\r\nContent-Type: text/plain\r\n\r\nOuter payload\r\n"
            + "--outer\r\nContent-Type: message/rfc822\r\n"
            + "Content-Disposition: attachment; filename=nested.eml\r\n\r\n"
            + "From: nested@example.net\r\nTo: user@mk8n.com\r\n"
            + "Date: Sun, 20 Sep 2026 11:00:00 +0000\r\n"
            + "Subject: Attached\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
            + "Nested payload\r\n--outer--\r\n");
        var attachedBlobId = await fixture.StoreBlobAsync(attachedRaw);
        var outerParse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}", "blobIds":["{{{attachedBlobId}}}"],
            "properties":["attachments"], "bodyProperties":["blobId", "type"]
          }, "p1"]]
        }
        """);
        var messageBlobId = Arguments(outerParse)["parsed"]![attachedBlobId]!["attachments"]![0]!["blobId"]!
            .GetValue<string>();
        var innerParse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}", "blobIds":["{{{messageBlobId}}}"],
            "properties":["textBody"], "bodyProperties":["blobId", "type"]
          }, "p2"]]
        }
        """);
        var nestedBodyBlobId = Arguments(innerParse)["parsed"]![messageBlobId]!["textBody"]![0]!["blobId"]!
            .GetValue<string>();
        using var scope = fixture.Services.CreateScope();
        var nestedBody = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, nestedBodyBlobId, CancellationToken.None);
        Assert.IsNotNull(nestedBody);
        Assert.AreEqual("Nested payload", Encoding.UTF8.GetString(nestedBody.Content).Trim());
    }

    [TestMethod]
    public async Task AttachedMessageBlobPreservesExactDecodedOctets()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var nestedRaw = Encoding.UTF8.GetBytes(
            "From: nested@example.net\n"
            + $"To: {fixture.User.Username}\n"
            + "Date: Sun, 20 Sep 2026 11:00:00 +0000\n"
            + "Subject: LF-only attachment\n"
            + "Content-Type: text/plain; charset=utf-8\n\n"
            + "first line\nsecond line");
        var prefix = Encoding.ASCII.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=outer\r\n\r\n"
            + "--outer\r\nContent-Type: message/rfc822\r\n"
            + "Content-Disposition: attachment; filename=exact.eml\r\n"
            + "Content-Transfer-Encoding: base64\r\n\r\n");
        var encodedNested = Encoding.ASCII.GetBytes(Convert.ToBase64String(nestedRaw));
        var suffix = Encoding.ASCII.GetBytes("\r\n--outer--\r\n");
        var outerRaw = prefix.Concat(encodedNested).Concat(suffix).ToArray();
        var outerBlobId = await fixture.StoreBlobAsync(outerRaw);

        var parse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{outerBlobId}}}"],
            "properties":["attachments"],
            "bodyProperties":["blobId", "size", "type"]
          }, "p1"]]
        }
        """);
        var attachment = Arguments(parse)["parsed"]![outerBlobId]!["attachments"]![0]!;
        Assert.AreEqual("message/rfc822", attachment["type"]!.GetValue<string>());
        Assert.AreEqual(nestedRaw.Length, attachment["size"]!.GetValue<int>());

        using var scope = fixture.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(
                fixture.InboxId,
                attachment["blobId"]!.GetValue<string>(),
                CancellationToken.None);
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(nestedRaw, stored.Content);
    }

    [TestMethod]
    public async Task BodyLanguageUsesRfcLanguageTags()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var validBlobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n"
            + "Content-Language: en-US (primary), i-klingon\r\n\r\nbody"));
        var invalidBlobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n"
            + "Content-Language: en_US\r\n\r\nbody"));

        var parse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{validBlobId}}}", "{{{invalidBlobId}}}"],
            "properties":["bodyStructure"],
            "bodyProperties":["language"]
          }, "p1"]]
        }
        """);
        Assert.AreEqual(
            "en-US|i-klingon",
            string.Join(
                '|',
                Arguments(parse)["parsed"]![validBlobId]!["bodyStructure"]!["language"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())));
        Assert.IsNull(
            Arguments(parse)["parsed"]![invalidBlobId]!["bodyStructure"]!["language"]);

        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{
              "badLanguage":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain", "language":["en_US"]}]
              },
              "goodLanguage":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{
                  "partId":"1", "type":"text/plain", "language":["en-US", "i-klingon"]
                }]
              }
            }
          }, "s1"]]
        }
        """);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(create)["notCreated"]!["badLanguage"]!["type"]!.GetValue<string>());
        var createdId = Arguments(create)["created"]!["goodLanguage"]!["id"]!.GetValue<string>();
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "ids":["{{{createdId}}}"],
            "properties":["bodyStructure"],
            "bodyProperties":["language"]
          }, "g1"]]
        }
        """);
        Assert.AreEqual(
            "en-US|i-klingon",
            string.Join(
                '|',
                Arguments(get)["list"]![0]!["bodyStructure"]!["language"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())));
    }

    [TestMethod]
    public async Task BlobQuotaEvictsOldestUnreferencedUpload()
    {
        const int quota = 1_048_576;
        await using var fixture = await JmapFixture.CreateAsync(quota);
        var first = await fixture.StoreBlobAsync(new byte[700_000], "application/octet-stream");
        var second = await fixture.StoreBlobAsync(new byte[700_000], "application/octet-stream");

        using var scope = fixture.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
        Assert.IsNull(await blobs.GetAsync(fixture.InboxId, first, CancellationToken.None));
        Assert.IsNotNull(await blobs.GetAsync(fixture.InboxId, second, CancellationToken.None));
    }

    [TestMethod]
    public async Task IdentitySubmissionQueuesMessageAndStripsBcc()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var identityResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"]]
        }
        """);
        var identityId = Arguments(identityResponse)["list"]![0]!["id"]!.GetValue<string>();

        var emailResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}", "create":{"send":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "keywords":{"$draft":true},
              "from":[{"email":"{{{fixture.User.Username}}}"}],
              "bcc":[{"email":"{{{fixture.User.Username}}}"}],
              "subject":"Submission test",
              "bodyValues":{"1":{"value":"queued"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "e1"]]
        }
        """);
        var createdEmail = Arguments(emailResponse)["created"]!["send"]!;
        var emailId = createdEmail["id"]!.GetValue<string>();
        var emailSize = createdEmail["size"]!.GetValue<int>();

        var submissionResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}", "create":{
              "submit":{
                "identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
                "envelope":{
                  "mailFrom":{"email":"{{{fixture.User.Username}}}","parameters":{"SIZE":"{{{emailSize}}}"}},
                  "rcptTo":[{"email":"{{{fixture.User.Username}}}","parameters":null}]
                }
              },
              "badParameter":{
                "identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
                "envelope":{
                  "mailFrom":{"email":"{{{fixture.User.Username}}}","parameters":{"DSN":null}},
                  "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]
                }
              },
              "badRecipientParameter":{
                "identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
                "envelope":{
                  "mailFrom":{"email":"{{{fixture.User.Username}}}"},
                  "rcptTo":[{"email":"{{{fixture.User.Username}}}","parameters":{"SIZE":"1"}}]
                }
              }
            }
          }, "s1"]]
        }
        """);
        var submissionId = Arguments(submissionResponse)["created"]!["submit"]!["id"]!.GetValue<string>();
        Assert.AreEqual(
            "invalidProperties",
            Arguments(submissionResponse)["notCreated"]!["badParameter"]!["type"]!.GetValue<string>());
        Assert.AreEqual(
            "invalidProperties",
            Arguments(submissionResponse)["notCreated"]!["badRecipientParameter"]!["type"]!
                .GetValue<string>());

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queued = await database.MailQueueMessages.Include(message => message.Recipients).SingleAsync();
            Assert.AreEqual(fixture.User.Username, queued.EnvelopeSender);
            Assert.AreEqual(fixture.User.Username, queued.Recipients.Single().Recipient);
            Assert.IsFalse(queued.RawMessage.Contains("Bcc:", StringComparison.OrdinalIgnoreCase));
        }

        var read = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{submissionId}}}"]
          }, "s2"]]
        }
        """);
        Assert.AreEqual("final", Arguments(read)["list"]![0]!["undoStatus"]!.GetValue<string>());
        Assert.AreEqual(emailId, Arguments(read)["list"]![0]!["emailId"]!.GetValue<string>());
        var storedSubmission = Arguments(read)["list"]![0]!.AsObject();
        Assert.AreEqual(
            emailSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            storedSubmission["envelope"]!["mailFrom"]!["parameters"]!["SIZE"]!
                .GetValue<string>());
        Assert.IsNull(storedSubmission["envelope"]!["rcptTo"]![0]!["parameters"]);

        var roundTrip = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Submission),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "EmailSubmission/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["update"] = new JsonObject
                    {
                        [submissionId] = storedSubmission.DeepClone(),
                    },
                },
                "s3")),
        });
        Assert.IsNull(Arguments(roundTrip)["notUpdated"]);
        Assert.IsTrue(Arguments(roundTrip)["updated"]!.AsObject().ContainsKey(submissionId));
    }

    [TestMethod]
    public async Task SubmissionGeneratesAndPersistsDeduplicatedEnvelope()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var identityResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"]]
        }
        """);
        var identityId = Arguments(identityResponse)["list"]![0]!["id"]!.GetValue<string>();
        var emailResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}", "create":{"generated":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "keywords":{"$draft":true},
              "from":[{"email":"{{{fixture.User.Username}}}"}],
              "to":[{"email":"{{{fixture.User.Username}}}"}],
              "cc":[{"email":"{{{fixture.User.Username}}}"}],
              "bcc":[{"email":"{{{fixture.User.Username}}}"}],
              "subject":"Generated envelope",
              "bodyValues":{"1":{"value":"queued"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "e1"]]
        }
        """);
        var emailId = Arguments(emailResponse)["created"]!["generated"]!["id"]!.GetValue<string>();
        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"generated":{"identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}"}}
          }, "s1"]]
        }
        """);
        var submissionId = Arguments(submission)["created"]!["generated"]!["id"]!.GetValue<string>();
        var read = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{submissionId}}}"],
            "properties":["id", "envelope"]
          }, "s2"]]
        }
        """);
        var envelope = Arguments(read)["list"]![0]!["envelope"]!;
        Assert.AreEqual(fixture.User.Username, envelope["mailFrom"]!["email"]!.GetValue<string>());
        Assert.IsNull(envelope["mailFrom"]!["parameters"]);
        Assert.AreEqual(1, envelope["rcptTo"]!.AsArray().Count);
        Assert.AreEqual(fixture.User.Username, envelope["rcptTo"]![0]!["email"]!.GetValue<string>());
        Assert.IsNull(envelope["rcptTo"]![0]!["parameters"]);
    }

    [TestMethod]
    public async Task IdentityAndSubmissionRejectInvalidWireValues()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var identities = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "g1"],
            ["Identity/set", {
              "accountId":"{{{fixture.AccountId}}}",
              "create": {
                "nullName":{"email":"{{{fixture.User.Username}}}", "name":null},
                "nullSignature":{"email":"{{{fixture.User.Username}}}", "textSignature":null},
                "displayAddress":{"email":"{{{fixture.User.Username}}}",
                  "replyTo":[{"email":"User <{{{fixture.User.Username}}}>"}]}
              }
            }, "s1"]]
        }
        """);
        var identityId = Arguments(identities)["list"]![0]!["id"]!.GetValue<string>();
        var identityFailures = Arguments(identities, 1)["notCreated"]!.AsObject();
        Assert.AreEqual("invalidProperties", identityFailures["nullName"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", identityFailures["nullSignature"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", identityFailures["displayAddress"]!["type"]!.GetValue<string>());

        var malformedRaw = Encoding.UTF8.GetBytes(
            "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Date: Sun, 20 Sep 2026 11:00:00 +0000\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Invalid singleton headers\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(malformedRaw);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"bad":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true} } }
          }, "i1"]]
        }
        """);
        var emailId = Arguments(import)["created"]!["bad"]!["id"]!.GetValue<string>();
        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"bad":{"identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
              "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]} } }
          }, "s2"]]
        }
        """);
        var error = Arguments(submission)["notCreated"]!["bad"]!;
        Assert.AreEqual("invalidEmail", error["type"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(
            new[] { "from", "sentAt" },
            error["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task IdentityAndVacationAcceptWholeGetObjectsAsUpdates()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}", "{{{Vacation}}}"],
          "methodCalls": [
            ["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"],
            ["VacationResponse/get", {"accountId":"{{{fixture.AccountId}}}"}, "v1"]
          ]
        }
        """);
        var identity = (JsonObject)Arguments(get)["list"]![0]!.DeepClone();
        var identityId = identity["id"]!.GetValue<string>();
        identity["name"] = "Round Trip";
        var vacation = (JsonObject)Arguments(get, 1)["list"]![0]!.DeepClone();
        vacation["isEnabled"] = true;
        vacation["subject"] = "Whole object update";
        vacation["textBody"] = "Back later";

        var set = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Submission, Vacation),
            ["methodCalls"] = new JsonArray(
                new JsonArray(
                    "Identity/set",
                    new JsonObject
                    {
                        ["accountId"] = fixture.AccountId,
                        ["update"] = new JsonObject { [identityId] = identity },
                    },
                    "i2"),
                new JsonArray(
                    "VacationResponse/set",
                    new JsonObject
                    {
                        ["accountId"] = fixture.AccountId,
                        ["update"] = new JsonObject { ["singleton"] = vacation },
                    },
                    "v2")),
        });
        Assert.IsNull(Arguments(set)["notUpdated"]);
        Assert.IsTrue(Arguments(set)["updated"]!.AsObject().ContainsKey(identityId));
        Assert.IsNull(Arguments(set, 1)["notUpdated"]);
        Assert.IsTrue(Arguments(set, 1)["updated"]!.AsObject().ContainsKey("singleton"));

        var verify = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}", "{{{Vacation}}}"],
          "methodCalls": [
            ["Identity/get", {"accountId":"{{{fixture.AccountId}}}", "ids":["{{{identityId}}}"]}, "i3"],
            ["VacationResponse/get", {"accountId":"{{{fixture.AccountId}}}"}, "v3"]
          ]
        }
        """);
        Assert.AreEqual("Round Trip", Arguments(verify)["list"]![0]!["name"]!.GetValue<string>());
        Assert.AreEqual(
            fixture.User.Username,
            Arguments(verify)["list"]![0]!["email"]!.GetValue<string>());
        Assert.AreEqual(
            "Whole object update",
            Arguments(verify, 1)["list"]![0]!["subject"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task LazySingletonInitializationIsIdempotentAcrossConcurrentRequests()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var request = $$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}", "{{{Vacation}}}"],
          "methodCalls": [
            ["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"],
            ["VacationResponse/get", {"accountId":"{{{fixture.AccountId}}}"}, "v1"]
          ]
        }
        """;
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => fixture.InvokeAsync(request)));
        foreach (var response in responses)
        {
            Assert.AreEqual("Identity/get", response["methodResponses"]![0]![0]!.GetValue<string>());
            Assert.AreEqual("VacationResponse/get", response["methodResponses"]![1]![0]!.GetValue<string>());
            Assert.AreEqual(1, Arguments(response)["list"]!.AsArray().Count);
            Assert.AreEqual(1, Arguments(response, 1)["list"]!.AsArray().Count);
        }

        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(1, await database.JmapIdentities.CountAsync());
        Assert.AreEqual(1, await database.JmapVacationResponses.CountAsync());
    }

    [TestMethod]
    public async Task VacationSingletonCanBeConfiguredButNotCreatedOrDestroyed()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Vacation}}}"],
          "methodCalls": [["VacationResponse/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"other":{"isEnabled":true}},
            "update":{"singleton":{
              "isEnabled":true,
              "fromDate":"2026-09-20T00:00:00Z",
              "toDate":"2026-09-30T00:00:00Z",
              "subject":"Away", "textBody":"Back soon"
            }},
            "destroy":["singleton"]
          }, "v1"], ["VacationResponse/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["singleton"]
          }, "v2"]]
        }
        """);
        Assert.AreEqual("singleton", Arguments(response)["notCreated"]!["other"]!["type"]!.GetValue<string>());
        Assert.AreEqual("singleton", Arguments(response)["notDestroyed"]!["singleton"]!["type"]!.GetValue<string>());
        var vacation = Arguments(response, 1)["list"]![0]!;
        Assert.IsTrue(vacation["isEnabled"]!.GetValue<bool>());
        Assert.AreEqual("Away", vacation["subject"]!.GetValue<string>());
        Assert.AreEqual("Back soon", vacation["textBody"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task VacationResponseAllowsAnEmptyDateWindow()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Vacation}}}"],
          "methodCalls": [["VacationResponse/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"singleton":{
              "isEnabled":true,
              "fromDate":"2026-09-30T00:00:00Z",
              "toDate":"2026-09-20T00:00:00Z"
            }}
          }, "v1"], ["VacationResponse/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["singleton"]
          }, "v2"]]
        }
        """);

        Assert.IsNull(Arguments(response)["notUpdated"]);
        Assert.IsTrue(Arguments(response)["updated"]!.AsObject().ContainsKey("singleton"));
        var vacation = Arguments(response, 1)["list"]![0]!;
        Assert.AreEqual("2026-09-30T00:00:00Z", vacation["fromDate"]!.GetValue<string>());
        Assert.AreEqual("2026-09-20T00:00:00Z", vacation["toDate"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PushMethodsRejectAccountStateAndDoNotPartiallyApplyInvalidPatch()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var subscriptionId = Guid.CreateVersion7();
        var wireId = JmapId.PushSubscription(subscriptionId);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
            {
                Id = subscriptionId,
                SubscriptionObjectId = wireId,
                UserId = fixture.User.Id,
                DeviceClientId = "device-1",
                Url = "https://push.example.net/jmap",
                VerificationCode = "secret",
                Types = ["Email"],
                ExpiresAt = DateTime.UtcNow.AddDays(3),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await database.SaveChangesAsync();
        }

        var invalidArguments = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}"],
          "methodCalls": [["PushSubscription/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{wireId}}}"]
          }, "p1"], ["PushSubscription/set", {
            "ifInState":"s0", "update":{"{{{wireId}}}":{"expires":"not-a-date"}}
          }, "p2"]]
        }
        """);
        Assert.AreEqual("invalidArguments", Arguments(invalidArguments)["type"]!.GetValue<string>());
        Assert.AreEqual("invalidArguments", Arguments(invalidArguments, 1)["type"]!.GetValue<string>());

        var invalidPatch = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}"],
          "methodCalls": [["PushSubscription/set", {
            "update":{"{{{wireId}}}":{"types":["Mailbox"], "expires":"not-a-date"}}
          }, "p3"], ["PushSubscription/get", {
            "ids":["{{{wireId}}}"], "properties":["id", "types"]
          }, "p4"], ["PushSubscription/get", {
            "ids":["{{{wireId}}}"], "properties":["keys"]
          }, "p5"]]
        }
        """);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(invalidPatch)["notUpdated"]![wireId]!["type"]!.GetValue<string>());
        Assert.AreEqual("Email", Arguments(invalidPatch, 1)["list"]![0]!["types"]![0]!.GetValue<string>());
        Assert.AreEqual("forbidden", Arguments(invalidPatch, 2)["type"]!.GetValue<string>());

        using var verificationScope = fixture.Services.CreateScope();
        var stored = await verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .JmapPushSubscriptions.AsNoTracking().SingleAsync();
        CollectionAssert.AreEqual(new[] { "Email" }, stored.Types!);
    }

    [TestMethod]
    public async Task PushSubscriptionAcceptsWholeGetObjectAndProtectsImmutableValues()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var subscriptionId = Guid.CreateVersion7();
        var wireId = JmapId.PushSubscription(subscriptionId);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
            {
                Id = subscriptionId,
                SubscriptionObjectId = wireId,
                UserId = fixture.User.Id,
                DeviceClientId = "round-trip-device",
                Url = "https://push.example.net/jmap",
                VerificationCode = "not-yet-verified",
                IsVerified = false,
                Types = ["Email"],
                ExpiresAt = DateTime.UtcNow.AddDays(3),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await database.SaveChangesAsync();
        }

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}"],
          "methodCalls": [["PushSubscription/get", {"ids":["{{{wireId}}}"]}, "p1"]]
        }
        """);
        var subscription = (JsonObject)Arguments(get)["list"]![0]!.DeepClone();
        Assert.IsNull(subscription["verificationCode"]);
        subscription["types"] = new JsonArray("Mailbox", "Email");
        var update = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "PushSubscription/set",
                new JsonObject
                {
                    ["update"] = new JsonObject { [wireId] = subscription },
                },
                "p2")),
        });
        Assert.IsNull(Arguments(update)["notUpdated"]);
        Assert.IsTrue(Arguments(update)["updated"]!.AsObject().ContainsKey(wireId));

        var forbidden = (JsonObject)subscription.DeepClone();
        forbidden["deviceClientId"] = "different-device";
        var invalid = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "PushSubscription/set",
                new JsonObject
                {
                    ["update"] = new JsonObject { [wireId] = forbidden },
                },
                "p3")),
        });
        var error = Arguments(invalid)["notUpdated"]![wireId]!;
        Assert.AreEqual("invalidProperties", error["type"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "deviceClientId" },
            error["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());

        using var verificationScope = fixture.Services.CreateScope();
        var stored = await verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .JmapPushSubscriptions.AsNoTracking().SingleAsync();
        CollectionAssert.AreEquivalent(new[] { "Mailbox", "Email" }, stored.Types!);
        Assert.IsFalse(stored.IsVerified);
        Assert.AreEqual("round-trip-device", stored.DeviceClientId);
    }

    [TestMethod]
    public void WebPushEncryptionRoundTripsWithReceiverKeys()
    {
        using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var receiverParameters = receiver.ExportParameters(false);
        var receiverPublic = new byte[65];
        receiverPublic[0] = 4;
        receiverParameters.Q.X!.CopyTo(receiverPublic, 1);
        receiverParameters.Q.Y!.CopyTo(receiverPublic, 33);
        var auth = RandomNumberGenerator.GetBytes(16);
        var publicText = Base64Url(receiverPublic);
        var authText = Base64Url(auth);
        Assert.IsTrue(JmapPushEncryption.TryValidateKeys(publicText, authText));

        var plaintext = Encoding.UTF8.GetBytes("{\"@type\":\"StateChange\"}");
        var encrypted = JmapPushEncryption.Encrypt(plaintext, publicText, authText);
        var decrypted = DecryptWebPush(encrypted, receiver, receiverPublic, auth);
        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    public void SearchSnippetNeverSplitsUnicodeScalars()
    {
        var value = new string('x', 10)
            + "😀"
            + new string('y', 79)
            + "needle"
            + new string('z', 220);
        var snippet = JmapSearchSnippetFormatter.HighlightPreview(value, ["needle"]);

        Assert.IsNotNull(snippet);
        Assert.IsTrue(JmapJson.ContainsOnlyUnicodeScalars(snippet));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(snippet) <= 255);
        StringAssert.Contains(snippet, "<mark>needle</mark>");
    }

    private static async Task<JsonObject> CreateTextEmailAsync(
        JmapFixture fixture,
        string creationId,
        string subject,
        string body)
    {
        return await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}", "create":{"{{{creationId}}}":{
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
              "keywords":{"$draft":true, "project":true},
              "from":[{"name":"User", "email":"{{{fixture.User.Username}}}"}],
              "to":[{"email":"recipient@example.net"}],
              "subject":"{{{subject}}}",
              "bodyValues":{"1":{"value":"{{{body}}}"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "e1"]]
        }
        """);
    }

    private static async Task<string> GetStateAsync(JmapFixture fixture, string dataType)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JmapStateService>()
            .GetStateAsync(fixture.InboxId, dataType);
    }

    private static JsonObject Arguments(JsonObject response, int index = 0) =>
        response["methodResponses"]!.AsArray()[index]!.AsArray()[1]!.AsObject();

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] DecryptWebPush(
        byte[] content,
        ECDiffieHellman receiver,
        byte[] receiverPublic,
        byte[] auth)
    {
        var salt = content.AsSpan(0, 16).ToArray();
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(16, 4));
        Assert.IsTrue(recordSize >= 18);
        var senderLength = content[20];
        var senderPublic = content.AsSpan(21, senderLength).ToArray();
        Assert.AreEqual(65, senderPublic.Length);
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = senderPublic.AsSpan(1, 32).ToArray(),
                Y = senderPublic.AsSpan(33, 32).ToArray(),
            },
        });
        var shared = receiver.DeriveRawSecretAgreement(sender.PublicKey);
        var keyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
        var keyInfo = keyInfoPrefix.Concat(receiverPublic).Concat(senderPublic).ToArray();
        var inputKeyMaterial = Expand(Extract(auth, shared), keyInfo, 32);
        var pseudoRandomKey = Extract(salt, inputKeyMaterial);
        var contentEncryptionKey = Expand(
            pseudoRandomKey,
            Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"),
            16);
        var nonce = Expand(
            pseudoRandomKey,
            Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"),
            12);
        var encryptedOffset = 21 + senderLength;
        var encryptedLength = content.Length - encryptedOffset - 16;
        var decrypted = new byte[encryptedLength];
        using (var aes = new AesGcm(contentEncryptionKey, 16))
        {
            aes.Decrypt(
                nonce,
                content.AsSpan(encryptedOffset, encryptedLength),
                content.AsSpan(content.Length - 16, 16),
                decrypted);
        }
        Assert.AreEqual((byte)2, decrypted[^1]);
        return decrypted[..^1];
    }

    private static byte[] Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> input)
    {
        using var hmac = new HMACSHA256(salt.ToArray());
        return hmac.ComputeHash(input.ToArray());
    }

    private static byte[] Expand(ReadOnlySpan<byte> key, ReadOnlySpan<byte> info, int length)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var written = 0;
        byte counter = 1;
        while (written < length)
        {
            var input = previous.Concat(info.ToArray()).Append(counter).ToArray();
            previous = hmac.ComputeHash(input);
            var count = Math.Min(previous.Length, length - written);
            previous.AsSpan(0, count).CopyTo(output.AsSpan(written));
            written += count;
            counter++;
        }
        return output;
    }
}
