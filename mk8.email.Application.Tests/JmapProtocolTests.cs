using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using mk8.email.Messaging;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapProtocolTests
{
    private const string Core = "urn:ietf:params:jmap:core";
    private const string Mail = "urn:ietf:params:jmap:mail";
    private const string Submission = "urn:ietf:params:jmap:submission";
    private const string Vacation = "urn:ietf:params:jmap:vacationresponse";

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MailboxLifecycleProducesChangesAndEnforcesRegisteredRoles scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MailboxLifecycleProducesChangesAndEnforcesRegisteredRoles()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var oldState = await GetStateAsync(fixture, JmapConstants.MailboxDataType).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var created = Arguments(create)["created"]!.AsObject();
        var parentId = created["parent"]!["id"]!.GetValue<string>();
        var childId = created["child"]!["id"]!.GetValue<string>();
        Assert.IsFalse(created["child"]!.AsObject().ContainsKey("parentId"));

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
        """).ConfigureAwait(false);
        Assert.AreEqual(parentId, Arguments(query)["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(1, Arguments(query)["total"]!.GetValue<int>());

        var changes = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/changes", {
            "accountId": "{{{fixture.AccountId}}}", "sinceState":"{{{oldState}}}"
          }, "ch1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(invalidRole)["notCreated"]!["bad"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var protectedParent = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}", "destroy":["{{{parentId}}}"]
          }, "m3"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "mailboxHasChild",
            Arguments(protectedParent)["notDestroyed"]![parentId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var destroy = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}", "destroy":["{{{parentId}}}", "{{{childId}}}"]
          }, "m4"]]
        }
        """).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { childId, parentId },
            Arguments(destroy)["destroyed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MailboxQueryChangesReturnsOrderedCreateUpdateAndDestroyDeltas scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MailboxQueryChangesReturnsOrderedCreateUpdateAndDestroyDeltas()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var sort = "\"sort\":[{\"property\":\"name\",\"isAscending\":true}]";
        var initial = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/query", {
            "accountId":"{{{fixture.AccountId}}}", {{{sort}}}, "calculateTotal":true
          }, "q1"]]
        }
        """).ConfigureAwait(false);
        Assert.IsTrue(Arguments(initial)["canCalculateChanges"]!.GetValue<bool>());
        var initialState = Arguments(initial)["queryState"]!.GetValue<string>();

        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"mailbox":{"name":"Zulu query delta"}}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var mailboxId = Arguments(create)["created"]!["mailbox"]!["id"]!.GetValue<string>();

        var createdChanges = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/queryChanges", {
            "accountId":"{{{fixture.AccountId}}}", {{{sort}}},
            "sinceQueryState":"{{{initialState}}}", "calculateTotal":true
          }, "qc1"]]
        }
        """).ConfigureAwait(false);
        var createdArguments = Arguments(createdChanges);
        Assert.AreEqual(0, createdArguments["removed"]!.AsArray().Count);
        Assert.AreEqual(mailboxId, createdArguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            createdArguments["total"]!.GetValue<int>() - 1,
            createdArguments["added"]![0]!["index"]!.GetValue<int>());
        var createdState = createdArguments["newQueryState"]!.GetValue<string>();

        await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"{{{mailboxId}}}":{"name":"Aardvark query delta"}}
          }, "s2"]]
        }
        """).ConfigureAwait(false);
        var limited = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/queryChanges", {
            "accountId":"{{{fixture.AccountId}}}", {{{sort}}},
            "sinceQueryState":"{{{createdState}}}", "maxChanges":1
          }, "qc2"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("error", limited["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("tooManyChanges", Arguments(limited)["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var updatedChanges = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/queryChanges", {
            "accountId":"{{{fixture.AccountId}}}", {{{sort}}},
            "sinceQueryState":"{{{createdState}}}", "upToId":"{{{mailboxId}}}"
          }, "qc3"]]
        }
        """).ConfigureAwait(false);
        var updatedArguments = Arguments(updatedChanges);
        CollectionAssert.AreEqual(
            new[] { mailboxId },
            updatedArguments["removed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual(mailboxId, updatedArguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, updatedArguments["added"]![0]!["index"]!.GetValue<int>());
        var updatedState = updatedArguments["newQueryState"]!.GetValue<string>();

        await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId":"{{{fixture.AccountId}}}", "destroy":["{{{mailboxId}}}"]
          }, "s3"]]
        }
        """).ConfigureAwait(false);
        var destroyedChanges = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/queryChanges", {
            "accountId":"{{{fixture.AccountId}}}", {{{sort}}},
            "sinceQueryState":"{{{updatedState}}}"
          }, "qc4"]]
        }
        """).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { mailboxId },
            Arguments(destroyedChanges)["removed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual(0, Arguments(destroyedChanges)["added"]!.AsArray().Count);

        var tree = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/query", {
            "accountId":"{{{fixture.AccountId}}}", "sortAsTree":true
          }, "q2"]]
        }
        """).ConfigureAwait(false);
        Assert.IsFalse(Arguments(tree)["canCalculateChanges"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task MailboxNamesMustUseNetUnicode()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        var notCreated = Arguments(response)["notCreated"]!.AsObject();
        Assert.AreEqual("invalidProperties", notCreated["bom"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", notCreated["unassigned"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var mailboxId = Arguments(response)["created"]!["normalized"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}", "ids":["{{{mailboxId}}}"],
            "properties":["name"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("Caf\u00e9", Arguments(get)["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MailboxSetReturnsOnlyServerSetDefaultedAndChangedProperties scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MailboxSetReturnsOnlyServerSetDefaultedAndChangedProperties()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var batch = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create": {
              "explicit": {
                "name":"Explicit response",
                "parentId":null,
                "role":null,
                "sortOrder":5,
                "isSubscribed":false
              },
              "defaults": {"name":"Default response"}
            }
          }, "m1"]]
        }
        """).ConfigureAwait(false);

        var created = Arguments(batch)["created"]!.AsObject();
        var serverSet = new[]
        {
            "id", "totalEmails", "unreadEmails", "totalThreads", "unreadThreads", "myRights",
        };
        CollectionAssert.AreEquivalent(
            serverSet,
            created["explicit"]!.AsObject().Select(property => property.Key).ToArray());
        CollectionAssert.AreEquivalent(
            serverSet.Concat(ExpectedVector1).ToArray(),
            created["defaults"]!.AsObject().Select(property => property.Key).ToArray());
        Assert.AreEqual(0, created["defaults"]!["totalEmails"]!.GetValue<int>());
        Assert.AreEqual(0, created["defaults"]!["unreadEmails"]!.GetValue<int>());
        Assert.AreEqual(0, created["defaults"]!["totalThreads"]!.GetValue<int>());
        Assert.AreEqual(0, created["defaults"]!["unreadThreads"]!.GetValue<int>());
        Assert.IsNotNull(created["defaults"]!["myRights"]);
        Assert.IsNull(created["defaults"]!["parentId"]);
        Assert.IsNull(created["defaults"]!["role"]);
        Assert.AreEqual(0L, created["defaults"]!["sortOrder"]!.GetValue<long>());
        Assert.IsTrue(created["defaults"]!["isSubscribed"]!.GetValue<bool>());

        var single = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create": {"single": {
              "name":"Single response",
              "parentId":null,
              "role":null,
              "sortOrder":7,
              "isSubscribed":true
            }}
          }, "m2"]]
        }
        """).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(
            serverSet,
            Arguments(single)["created"]!["single"]!.AsObject()
                .Select(property => property.Key).ToArray());
    }

    [TestMethod]
    public async Task MailboxSortOrderIsLimitedToTheRfc8621Range()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
              "accountId": "{{{fixture.AccountId}}}",
              "create": {
                "maximum": {"name":"Maximum order", "sortOrder":2147483647},
                "tooLarge": {"name":"Too large", "sortOrder":2147483648}
              }
            }, "m1"]]
        }
        """).ConfigureAwait(false);

        var mailboxId = Arguments(response)["created"]!["maximum"]!["id"]!.GetValue<string>();
        Assert.AreEqual(
            "invalidProperties",
            Arguments(response)["notCreated"]!["tooLarge"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}",
            "ids": ["{{{mailboxId}}}"],
            "properties": ["sortOrder"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(mailboxId, Arguments(get)["list"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(int.MaxValue, Arguments(get)["list"]![0]!["sortOrder"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task CreationIdsAreNotAcceptedAsOrdinaryReadOrQueryIds()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}", "{{{Submission}}}"],
          "createdIds": {"mailbox":"{{{fixture.DraftsMailboxId}}}"},
          "methodCalls": [
            ["Mailbox/get", {
              "accountId":"{{{fixture.AccountId}}}", "ids":["#mailbox"]
            }, "g1"],
            ["Mailbox/query", {
              "accountId":"{{{fixture.AccountId}}}", "anchor":"#mailbox"
            }, "q1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"inMailbox":"#mailbox"}
            }, "q2"],
            ["EmailSubmission/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"identityIds":["#mailbox"]}
            }, "q3"]
          ]
        }
        """).ConfigureAwait(false);

        for (var index = 0; index < 4; index++)
        {
            Assert.AreEqual(
                "error",
                response["methodResponses"]![index]![0]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.AreEqual(
                "invalidArguments",
                Arguments(response, index)["type"]!.GetValue<string>(), StringComparer.Ordinal);
        }
    }

    [TestMethod]
    public async Task QueryComparatorsRejectNullStringProperties()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}", "{{{Submission}}}"],
          "methodCalls": [
            ["Mailbox/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "sort":[{"property":"sortOrder", "collation":null}]
            }, "m1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "sort":[{"property":"size", "collation":null}]
            }, "e1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "sort":[{"property":"receivedAt", "keyword":null}]
            }, "e2"],
            ["EmailSubmission/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "sort":[{"property":"sentAt", "collation":null}]
            }, "s1"]
          ]
        }
        """).ConfigureAwait(false);

        var methodResponses = response["methodResponses"]!.AsArray();
        Assert.AreEqual(4, methodResponses.Count);
        foreach (var methodResponse in methodResponses)
        {
            Assert.AreEqual("error", methodResponse![0]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.AreEqual(
                "invalidArguments",
                methodResponse[1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        }
    }

    [TestMethod]
    public async Task QueryFiltersAcceptValidUnknownIds()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{
              "email":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              }
            }
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["email"]!["id"]!.GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Mailbox/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"parentId":"unknownMailbox"}
            }, "q1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"inMailbox":"unknownMailbox"}
            }, "q2"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"inMailboxOtherThan":["unknownMailbox"]}
            }, "q3"]
          ]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual("Mailbox/query", response["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, Arguments(response, 0)["ids"]!.AsArray().Count);
        Assert.AreEqual("Email/query", response["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, Arguments(response, 1)["ids"]!.AsArray().Count);
        Assert.AreEqual("Email/query", response["methodResponses"]![2]![0]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            new[] { emailId },
            Arguments(response, 2)["ids"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task MailboxWithNullRoleDoesNotInferOneFromItsName()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var sent = await database.Folders.SingleAsync(folder => folder.Id == fixture.SentFolderId).ConfigureAwait(false);
            database.Folders.Remove(sent);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/set", {
            "accountId": "{{{fixture.AccountId}}}",
            "create": {"sentByName":{"name":"Sent", "role":null}}
          }, "m1"]]
        }
        """).ConfigureAwait(false);
        var mailboxId = Arguments(create)["created"]!["sentByName"]!["id"]!.GetValue<string>();
        Assert.IsNull(Arguments(create)["created"]!["sentByName"]!["role"]);

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Mailbox/get", {
            "accountId": "{{{fixture.AccountId}}}", "ids":["{{{mailboxId}}}"],
            "properties":["id", "name", "role"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);

        Assert.IsNull(Arguments(get)["list"]![0]!["role"]);
        Assert.IsTrue(JmapId.TryParseMailbox(mailboxId, out var folderId));
        using var verificationScope = fixture.Services.CreateScope();
        var stored = await verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .Folders.AsNoTracking()
            .SingleAsync(folder => folder.Id == folderId).ConfigureAwait(false);
        Assert.IsNull(stored.JmapRole);
        Assert.IsTrue(stored.SuppressDefaultJmapRole);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MailboxSetUsesFinalStateForSwapsAndAcceptsGetObjects scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MailboxSetUsesFinalStateForSwapsAndAcceptsGetObjects()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var swapped = Arguments(verify)["list"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(mailbox => mailbox["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Beta", swapped[alphaId]["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("important", swapped[alphaId]["role"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(11L, swapped[alphaId]["sortOrder"]!.GetValue<long>());
        Assert.AreEqual("Alpha", swapped[betaId]["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("flagged", swapped[betaId]["role"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MailboxSetUsesFinalStateAcrossCreatesUpdatesAndDestroys scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MailboxSetUsesFinalStateAcrossCreatesUpdatesAndDestroys()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var byId = Arguments(verify)["list"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(mailbox => mailbox["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Moved", byId[moveId]["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("archive", byId[moveId]["role"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Taken", byId[reusedUpdateId]["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("flagged", byId[reusedUpdateId]["role"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Removed", byId[reusedDestroyId]["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("important", byId[reusedDestroyId]["role"]!.GetValue<string>(), StringComparer.Ordinal);
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
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.Latin1.GetBytes(
            $"From: sender@example.net\nTo: {fixture.User.Username}\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\n"
            + "Subject: Exact raw bytes\n\nbody with LF only\n");
        var uploadBlobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"raw":{"blobId":"{{{uploadBlobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(import)["created"]!["raw"]!["id"]!.GetValue<string>();
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["id", "blobId", "size"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        var email = Arguments(get)["list"]![0]!;
        Assert.AreEqual(raw.LongLength, email["size"]!.GetValue<long>());

        using var scope = fixture.Services.CreateScope();
        var storedBlob = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(
                fixture.InboxId,
                email["blobId"]!.GetValue<string>(),
                CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(storedBlob);
        CollectionAssert.AreEqual(raw, storedBlob.Content);
        Assert.IsTrue(JmapId.TryParseEmail(emailId, out var storedEmailId));
        var storedEmail = await scope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .Emails.AsNoTracking().SingleAsync(message => message.Id == storedEmailId).ConfigureAwait(false);
        Assert.IsNull(storedEmail.RawMessage);
        Assert.AreEqual(LargeObjectProviders.AzureBlob, storedEmail.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.IsNotNull(storedEmail.RawMessageObjectName);
        var storedRaw = await scope.ServiceProvider
            .GetRequiredService<MailboxMessageContentService>()
            .ReadAsync(storedEmail, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(raw, storedRaw);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailCopyRendersGatewayOwnedSourceDeletionAndStateMismatch scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailCopyRendersGatewayOwnedSourceDeletionAndStateMismatch()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var targetInboxId = Guid.CreateVersion7();
        var targetFolderId = Guid.CreateVersion7();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var original = await database.Inboxes.SingleAsync().ConfigureAwait(false);
            await database.Inboxes.AddAsync(new InboxDB
            {
                Id = targetInboxId,
                Name = "copy-target",
                AddressId = original.AddressId,
                OwnerId = fixture.User.Id,
            }).ConfigureAwait(false);
            await database.Folders.AddAsync(new FolderDB
            {
                Id = targetFolderId,
                InboxId = targetInboxId,
                Name = "Inbox",
                UidValidity = 1,
                NextUid = 1,
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        var blobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            $"From: sender@example.test\r\nTo: {fixture.User.Username}\r\nSubject: Copy\r\n\r\nBody")).ConfigureAwait(false);
        var targetAccountId = JmapId.Account(targetInboxId);
        var targetMailboxId = JmapId.Mailbox(targetFolderId);
        async Task<string> ImportSourceAsync()
        {
            var response = await fixture.InvokeAsync(new JsonObject
            {
                ["using"] = new JsonArray(Core, Mail),
                ["methodCalls"] = new JsonArray(new JsonArray("Email/import", new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["emails"] = new JsonObject
                    {
                        ["original"] = new JsonObject
                        {
                            ["blobId"] = blobId,
                            ["mailboxIds"] = new JsonObject { [fixture.InboxMailboxId] = true },
                        },
                    },
                }, "import")),
            }).ConfigureAwait(false);
            return Arguments(response)["created"]!["original"]!["id"]!.GetValue<string>();
        }

        async Task<JsonObject> CopyAsync(string sourceId, bool mismatch)
        {
            var arguments = new JsonObject
            {
                ["fromAccountId"] = fixture.AccountId,
                ["accountId"] = targetAccountId,
                ["create"] = new JsonObject
                {
                    ["copy"] = new JsonObject
                    {
                        ["id"] = sourceId,
                        ["mailboxIds"] = new JsonObject { [targetMailboxId] = true },
                    },
                },
                ["onSuccessDestroyOriginal"] = true,
            };
            if (mismatch)
                arguments["destroyFromIfInState"] = "stale";
            else
                arguments["create"]!["copy"]!["keywords"] = new JsonObject { ["$flagged"] = true };
            return await fixture.InvokeAsync(new JsonObject
            {
                ["using"] = new JsonArray(Core, Mail),
                ["methodCalls"] = new JsonArray(new JsonArray("Email/copy", arguments, "copy")),
            }).ConfigureAwait(false);
        }

        var sourceId = await ImportSourceAsync().ConfigureAwait(false);
        var copied = await CopyAsync(sourceId, mismatch: false).ConfigureAwait(false);
        Assert.AreEqual("Email/copy", copied["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        var copiedId = Arguments(copied)["created"]!["copy"]!["id"]!.GetValue<string>();
        Assert.AreNotEqual(sourceId, copiedId, StringComparer.Ordinal);
        Assert.AreEqual("Email/set", copied["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(sourceId, Arguments(copied, 1)["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(targetFolderId, (await database.Emails.SingleAsync().ConfigureAwait(false)).FolderId);
        }
        var secondSourceId = await ImportSourceAsync().ConfigureAwait(false);
        var mismatched = await CopyAsync(secondSourceId, mismatch: true).ConfigureAwait(false);
        Assert.IsNotNull(Arguments(mismatched)["created"]!["copy"]);
        Assert.AreEqual("error", mismatched["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("stateMismatch", Arguments(mismatched, 1)["type"]!.GetValue<string>(), StringComparer.Ordinal);
        using var verificationScope = fixture.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(3, await verification.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailWritesRejectNullValuesForNonNullableDefaults scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailWritesRejectNullValuesForNonNullableDefaults()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var blobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n\r\nbody")).ConfigureAwait(false);

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
        """).ConfigureAwait(false);

        foreach (var index in new[] { 0, 1 })
        {
            var arguments = Arguments(response, index);
            Assert.AreEqual(
                "invalidProperties",
                arguments["notCreated"]!["nullKeywords"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.AreEqual(
                "invalidProperties",
                arguments["notCreated"]!["nullReceivedAt"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.IsNotNull(arguments["created"]!["defaults"]);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailKeywordsFollowTheImapCompatibleCharacterSet scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailKeywordsFollowTheImapCompatibleCharacterSet()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{
              "valid":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "reserved":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "keywords":{"$ReCeNt":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "leftBracket":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "keywords":{"bad[key":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "rightBrace":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "keywords":{"bad}key":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "leftBrace":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "keywords":{"bad{key":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "rightBracket":{
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "keywords":{"bad]key":true},
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              }
            }
          }, "s1"]]
        }
        """).ConfigureAwait(false);

        var emailId = Arguments(create)["created"]!["valid"]!["id"]!.GetValue<string>();
        Assert.AreEqual(
            "invalidProperties",
            Arguments(create)["notCreated"]!["reserved"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(create)["notCreated"]!["leftBrace"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(create)["notCreated"]!["rightBracket"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNotNull(Arguments(create)["created"]!["leftBracket"]);
        Assert.IsNotNull(Arguments(create)["created"]!["rightBrace"]);

        var update = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{
              "{{{emailId}}}":{"keywords":{"$recent":true, "custom":true}}
            }
          }, "s2"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(update)["notUpdated"]![emailId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        Assert.IsTrue(JmapId.TryParseEmail(emailId, out var storedEmailId));
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var email = await database.Emails.SingleAsync(candidate => candidate.Id == storedEmailId).ConfigureAwait(false);
            email.Keywords = ["$recent", "custom"];
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["keywords"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        var keywords = Arguments(get)["list"]![0]!["keywords"]!.AsObject();
        Assert.IsFalse(keywords.ContainsKey("$recent"));
        Assert.IsTrue(keywords["custom"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task EmailReadsFailInsteadOfHidingCorruptStoredRecords()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = Guid.CreateVersion7();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Emails.AddAsync(new EmailDB
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
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        Assert.AreEqual("error", response["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("serverFail", Arguments(response)["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("error", response["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("serverFail", Arguments(response, 1)["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EmailBodyValuesReportMalformedCharsetData()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var prefix = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Invalid UTF-8\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: 8bit\r\n\r\n"
            + "before ");
        var suffix = Encoding.ASCII.GetBytes(" after\r\n");
        var raw = prefix.Concat(new byte[] { 0xc3, 0x28 }).Concat(suffix).ToArray();
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        var partId = parsed["textBody"]![0]!["partId"]!.GetValue<string>();
        var bodyValue = parsed["bodyValues"]![partId]!;
        Assert.IsTrue(bodyValue["isEncodingProblem"]!.GetValue<bool>());
        StringAssert.Contains(bodyValue["value"]!.GetValue<string>(), "\ufffd(", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task EmailParseRejectsNullPropertiesButEmailGetAcceptsThem()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var blobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n\r\nbody")).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/parse", {
              "accountId": "{{{fixture.AccountId}}}",
              "blobIds": ["{{{blobId}}}"],
              "properties": null
            }, "p1"],
            ["Email/get", {
              "accountId": "{{{fixture.AccountId}}}",
              "ids": [],
              "properties": null
            }, "g1"]
          ]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual("error", response["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidArguments", Arguments(response)["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Email/get", response["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task HtmlBodyValueTruncationStopsBeforeAnOpenTag()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/html; charset=us-ascii\r\n\r\n"
            + "<p>Hello</p><a title=\"1 > 0\" href=\"https://example.com\">world</a>");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        var partId = parsed["htmlBody"]![0]!["partId"]!.GetValue<string>();
        var bodyValue = parsed["bodyValues"]![partId]!;
        var value = bodyValue["value"]!.GetValue<string>();
        Assert.AreEqual("<p>Hello</p>", value, StringComparer.Ordinal);
        Assert.IsTrue(bodyValue["isTruncated"]!.GetValue<bool>());
        Assert.IsTrue(Encoding.UTF8.GetByteCount(value) <= 30);
    }

    [TestMethod]
    public async Task EmailParsedHeaderTextDropsDecodedControlsAndNormalizesUnicode()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        const string decodedSubject = "Clean\0\u0001\t Cafe\u0301";
        const string decodedName = "Sender Cafe\u0301";
        var encodedSubject = Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedSubject));
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedName));
        var raw = Encoding.ASCII.GetBytes(
            $"From: =?utf-8?B?{encodedName}?= <sender@example.net>\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + $"Subject: =?utf-8?B?{encodedSubject}?=\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["subject", "header:Subject:asText", "from"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        var expectedSubject = "Clean Cafe\u0301".Normalize(NormalizationForm.FormC);
        var expectedName = decodedName.Normalize(NormalizationForm.FormC);
        Assert.AreEqual(expectedSubject, parsed["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            expectedSubject,
            parsed["header:Subject:asText"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            expectedName,
            parsed["from"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EmailParsedHeaderTextPreservesUnfoldedHorizontalTabs()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject:  First\r\n\tSecond\tThird\r\n"
            + "Comments: =?utf-8?B?Rmlyc3Q=?=\r\n"
            + "\t=?utf-8?B?U2Vjb25k?=\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": [
              "subject",
              "header:Subject:asText",
              "header:Comments:asText"
            ]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.AreEqual("First\tSecond\tThird", parsed["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "First\tSecond\tThird",
            parsed["header:Subject:asText"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "FirstSecond",
            parsed["header:Comments:asText"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EmailParsedAddressesPreserveRawButNotEncodedHorizontalTabs()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes("Encoded\tTab"));
        var raw = Encoding.ASCII.GetBytes(
            "From: \"Raw\tTab\" <raw@example.net>, "
            + $"=?utf-8?B?{encodedName}?= <encoded@example.net>\r\n"
            + $"To: {fixture.User.Username}\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["from"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
        var addresses = Arguments(response)["parsed"]![blobId]!["from"]!.AsArray();
        Assert.AreEqual("Raw\tTab", addresses[0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("EncodedTab", addresses[1]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EmailParsedMessageIdsUseRfc5322Syntax()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Message-ID: <not a message id>\r\n"
            + "References: (first) <valid@example.test>\r\n"
            + " <\"quoted local\"@example.test>\r\n"
            + "Resent-Message-ID: <valid@example.test> invalid\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.IsNull(parsed["messageId"]);
        CollectionAssert.AreEqual(
            ExpectedVector2,
            parsed["references"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        Assert.IsNull(parsed["header:Resent-Message-ID:asMessageIds"]);
    }

    [TestMethod]
    public async Task EmailParsedUrlsRejectInvalidBracketedValues()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "List-Unsubscribe: <not a url>\r\n"
            + "List-Help: (preferred) <https://example.test/help>, <mailto:help@example.test>\r\n"
            + "List-Subscribe: <https://example.test/sub scribe>\r\n"
            + "List-Owner: invalid <mailto:owner@example.test>\r\n"
            + "List-Archive: <https://example.test/first>, invalid, <https://example.test/last>\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.IsNull(parsed["header:List-Unsubscribe:asURLs"]);
        CollectionAssert.AreEqual(
            ExpectedVector3,
            parsed["header:List-Help:asURLs"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        CollectionAssert.AreEqual(
            ExpectedVector4,
            parsed["header:List-Subscribe:asURLs"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        Assert.IsNull(parsed["header:List-Owner:asURLs"]);
        CollectionAssert.AreEqual(
            ExpectedVector5,
            parsed["header:List-Archive:asURLs"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task EmailParsedDatesUseRfc5322Syntax()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Date: 2026-01-02T03:04:05Z\r\n"
            + "Resent-Date: Fri, 2 Jan 2026 03:04:05 +0000\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId": "{{{fixture.AccountId}}}",
            "blobIds": ["{{{blobId}}}"],
            "properties": ["sentAt", "header:Resent-Date:asDate"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
        var parsed = Arguments(response)["parsed"]![blobId]!;
        Assert.IsNull(parsed["sentAt"]);
        Assert.AreEqual(
            "2026-01-02T03:04:05Z",
            parsed["header:Resent-Date:asDate"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EmailCreationPreservesParsedDateOffsets()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"dated":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "sentAt":"2026-01-02T03:04:05+02:30",
              "header:Resent-Date:asDate":"2026-01-03T04:05:06-05:00",
              "bodyValues":{"1":{"value":"body"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["dated"]!["id"]!.GetValue<string>();
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "ids":["{{{emailId}}}"],
            "properties":["sentAt", "header:Resent-Date:asDate"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);

        var email = Arguments(response)["list"]![0]!;
        Assert.AreEqual(
            "2026-01-02T03:04:05+02:30",
            email["sentAt"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "2026-01-03T04:05:06-05:00",
            email["header:Resent-Date:asDate"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task EmailCreationPreservesRawHeadersAndRejectsHeaderInjection()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{
              "raw":{
                "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
                "header:X-Raw:all":[
                  " \t=?UTF-8?Q?already_encoded?=\r\n\tcontinued",
                  "second"
                ],
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              },
              "injection":{
                "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
                "header:X-Raw":" value\r\nBcc: injected@example.test",
                "bodyValues":{"1":{"value":"body"}},
                "textBody":[{"partId":"1", "type":"text/plain"}]
              }
            }
          }, "s1"]]
        }
        """).ConfigureAwait(false);

        var rawId = Arguments(response)["created"]!["raw"]!["id"]!.GetValue<string>();
        Assert.AreEqual(
            "invalidProperties",
            Arguments(response)["notCreated"]!["injection"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{rawId}}}"],
            "properties":["header:X-Raw", "header:X-Raw:all"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        var email = Arguments(get)["list"]![0]!;
        Assert.AreEqual("second", email["header:X-Raw"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            ExpectedVector6,
            email["header:X-Raw:all"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task EmailCreationDoesNotDuplicateGenericConvenienceHeaders()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"generic":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "header:From:asAddresses":[{"name":"Author", "email":"author@example.test"}],
              "header:Subject:asText":"Generic headers",
              "header:Date:asDate":"2026-09-20T12:34:56Z",
              "header:Message-ID:asMessageIds":["generic@example.test"],
              "bodyValues":{"1":{"value":"body"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["generic"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":[
              "header:From:asAddresses:all",
              "header:Subject:asText:all",
              "header:Date:asDate:all",
              "header:Message-ID:asMessageIds:all"
            ]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        var email = Arguments(get)["list"]![0]!;
        Assert.AreEqual(1, email["header:From:asAddresses:all"]!.AsArray().Count);
        Assert.AreEqual(1, email["header:Subject:asText:all"]!.AsArray().Count);
        Assert.AreEqual(1, email["header:Date:asDate:all"]!.AsArray().Count);
        Assert.AreEqual(1, email["header:Message-ID:asMessageIds:all"]!.AsArray().Count);
        Assert.AreEqual(
            "author@example.test",
            email["header:From:asAddresses:all"]![0]![0]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "generic@example.test",
            email["header:Message-ID:asMessageIds:all"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task DeletingLegacyEmailWithNullThreadDestroysItsFallbackThread()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await CreateTextEmailAsync(
            fixture,
            "legacyThread",
            "Legacy thread",
            "body").ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["legacyThread"]!["id"]!
            .GetValue<string>();
        Assert.IsTrue(JmapId.TryParseEmail(emailId, out var databaseId));
        var fallbackThreadId = JmapId.Thread(databaseId.ToString("N"));

        string beforeDelete;
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var email = await database.Emails.SingleAsync(item => item.Id == databaseId).ConfigureAwait(false);
            email.ThreadObjectId = null;
            await database.SaveChangesAsync().ConfigureAwait(false);
            beforeDelete = await scope.ServiceProvider.GetRequiredService<JmapStateService>()
                .GetStateAsync(fixture.InboxId, JmapConstants.ThreadDataType).ConfigureAwait(false);

            database.Emails.Remove(email);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var changes = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Thread/changes",{
            "accountId":"{{{fixture.AccountId}}}",
            "sinceState":"{{{beforeDelete}}}"
          },"c1"]]
        }
        """).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { fallbackThreadId },
            Arguments(changes)["destroyed"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailLifecycleProjectsMimeAndPreservesImapState scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailLifecycleProjectsMimeAndPreservesImapState()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var oldState = await GetStateAsync(fixture, JmapConstants.EmailDataType).ConfigureAwait(false);
        var create = await CreateTextEmailAsync(fixture, "draft1", "JMAP lifecycle", "Hello from JMAP").ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["draft1"]!["id"]!.GetValue<string>();
        var threadId = Arguments(create)["created"]!["draft1"]!["threadId"]!.GetValue<string>();

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var stored = await database.Emails.Include(email => email.Folder).SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(1, stored.Uid);
            Assert.IsTrue(stored.ModSeq > 0);
            Assert.AreEqual(fixture.InboxFolderId, stored.FolderId);
            StringAssert.Contains(stored.RawHeaders!, "Subject: JMAP lifecycle", StringComparison.Ordinal);
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
        """).ConfigureAwait(false);
        var email = Arguments(get)["list"]![0]!.AsObject();
        Assert.AreEqual("JMAP lifecycle", email["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("JMAP lifecycle", email["header:Subject:asText"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Hello", email["bodyValues"]!["1"]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
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
        """).ConfigureAwait(false);
        Assert.AreEqual(emailId, Arguments(query)["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
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
        }).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(invalidUpdate)["notUpdated"]![emailId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var stored = await database.Emails.SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(fixture.DraftsFolderId, stored.FolderId);
            Assert.AreEqual(1, stored.Uid);
            Assert.IsTrue(stored.IsRead);
            Assert.AreEqual(1, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
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
        """).ConfigureAwait(false);
        CollectionAssert.Contains(
            Arguments(changes)["created"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray(),
            emailId);
        Assert.AreEqual(emailId, Arguments(changes, 1)["list"]![0]!["emailIds"]![0]!.GetValue<string>(), StringComparer.Ordinal);

        var destroy = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}", "destroy":["{{{emailId}}}"]
          }, "s3"], ["Email/changes", {
            "accountId":"{{{fixture.AccountId}}}", "sinceState":"{{{updatedState}}}"
          }, "ch2"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(emailId, Arguments(destroy)["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(emailId, Arguments(destroy, 1)["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailQueryChangesReturnsOrderedCreateUpdateAndDestroyDeltas scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailQueryChangesReturnsOrderedCreateUpdateAndDestroyDeltas()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        const string sort = "\"sort\":[{\"property\":\"hasKeyword\","
            + "\"keyword\":\"$flagged\",\"isAscending\":true}]";
        var initial = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/query",{
            "accountId":"{{{fixture.AccountId}}}",{{{sort}}}
          },"q1"]]
        }
        """).ConfigureAwait(false);
        Assert.IsTrue(Arguments(initial)["canCalculateChanges"]!.GetValue<bool>());
        var initialState = Arguments(initial)["queryState"]!.GetValue<string>();

        var create = await CreateTextEmailAsync(
            fixture,
            "queryDelta",
            "Query delta",
            "body").ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["queryDelta"]!["id"]!.GetValue<string>();
        var createdChanges = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",{{{sort}}},
            "sinceQueryState":"{{{initialState}}}","calculateTotal":true
          },"qc1"]]
        }
        """).ConfigureAwait(false);
        var createdArguments = Arguments(createdChanges);
        Assert.AreEqual(0, createdArguments["removed"]!.AsArray().Count);
        Assert.AreEqual(emailId, createdArguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, createdArguments["added"]![0]!["index"]!.GetValue<int>());
        Assert.AreEqual(1, createdArguments["total"]!.GetValue<int>());
        var createdState = createdArguments["newQueryState"]!.GetValue<string>();

        await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/set",{
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"{{{emailId}}}":{"keywords/$flagged":true}}
          },"s1"]]
        }
        """).ConfigureAwait(false);
        var limited = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",{{{sort}}},
            "sinceQueryState":"{{{createdState}}}","maxChanges":1
          },"qc2"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("tooManyChanges", Arguments(limited)["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var updatedChanges = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",{{{sort}}},
            "sinceQueryState":"{{{createdState}}}"
          },"qc3"]]
        }
        """).ConfigureAwait(false);
        var updatedArguments = Arguments(updatedChanges);
        CollectionAssert.AreEqual(
            new[] { emailId },
            updatedArguments["removed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual(emailId, updatedArguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, updatedArguments["added"]![0]!["index"]!.GetValue<int>());
        var updatedState = updatedArguments["newQueryState"]!.GetValue<string>();

        await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/set",{
            "accountId":"{{{fixture.AccountId}}}","destroy":["{{{emailId}}}"]
          },"s2"]]
        }
        """).ConfigureAwait(false);
        var destroyedChanges = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",{{{sort}}},
            "sinceQueryState":"{{{updatedState}}}"
          },"qc4"]]
        }
        """).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { emailId },
            Arguments(destroyedChanges)["removed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual(0, Arguments(destroyedChanges)["added"]!.AsArray().Count);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailQueryChangesRecalculatesEveryEmailAffectedByThreadKeywords scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailQueryChangesRecalculatesEveryEmailAffectedByThreadKeywords()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var first = await CreateTextEmailAsync(fixture, "first", "Thread first", "first").ConfigureAwait(false);
        var second = await CreateTextEmailAsync(fixture, "second", "Thread second", "second").ConfigureAwait(false);
        var firstId = Arguments(first)["created"]!["first"]!["id"]!.GetValue<string>();
        var secondId = Arguments(second)["created"]!["second"]!["id"]!.GetValue<string>();
        Assert.IsTrue(JmapId.TryParseEmail(firstId, out var firstDatabaseId));
        Assert.IsTrue(JmapId.TryParseEmail(secondId, out var secondDatabaseId));
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var emails = await database.Emails
                .Where(email => email.Id == firstDatabaseId || email.Id == secondDatabaseId)
                .ToListAsync().ConfigureAwait(false);
            var threadId = Guid.CreateVersion7().ToString("N");
            foreach (var email in emails)
                email.ThreadObjectId = threadId;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var initial = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/query",{
            "accountId":"{{{fixture.AccountId}}}",
            "filter":{"someInThreadHaveKeyword":"$flagged"}
          },"q1"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(0, Arguments(initial)["ids"]!.AsArray().Count);
        var initialState = Arguments(initial)["queryState"]!.GetValue<string>();

        await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/set",{
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"{{{firstId}}}":{"keywords/$flagged":true}}
          },"s1"]]
        }
        """).ConfigureAwait(false);
        var changes = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Mail}}}"],
          "methodCalls":[["Email/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",
            "filter":{"someInThreadHaveKeyword":"$flagged"},
            "sinceQueryState":"{{{initialState}}}"
          },"qc1"]]
        }
        """).ConfigureAwait(false);
        var arguments = Arguments(changes);
        CollectionAssert.AreEquivalent(
            new[] { firstId, secondId },
            arguments["removed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { firstId, secondId },
            arguments["added"]!.AsArray()
                .Select(node => node!["id"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            ExpectedVector7,
            arguments["added"]!.AsArray()
                .Select(node => node!["index"]!.GetValue<int>()).ToArray());
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailSubmissionQueryChangesReturnsImmutableQueryDeltas scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailSubmissionQueryChangesReturnsImmutableQueryDeltas()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var initial = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Submission}}}"],
          "methodCalls":[["EmailSubmission/query",{
            "accountId":"{{{fixture.AccountId}}}",
            "sort":[{"property":"sentAt","isAscending":true}]
          },"q1"]]
        }
        """).ConfigureAwait(false);
        Assert.IsTrue(Arguments(initial)["canCalculateChanges"]!.GetValue<bool>());
        var initialState = Arguments(initial)["queryState"]!.GetValue<string>();

        var submissionId = Guid.CreateVersion7();
        var submissionObjectId = JmapId.Submission(submissionId);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.JmapEmailSubmissions.AddAsync(new JmapEmailSubmissionDB
            {
                Id = submissionId,
                SubmissionObjectId = submissionObjectId,
                AccountId = fixture.InboxId,
                IdentityId = JmapId.Identity(fixture.InboxId),
                EmailId = JmapId.Email(Guid.CreateVersion7()),
                ThreadId = JmapId.Thread(Guid.CreateVersion7().ToString("N")),
                QueueId = Guid.CreateVersion7(),
                EnvelopeSender = fixture.User.Username,
                EnvelopeRecipients = ["recipient@example.net"],
                SendAt = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var limited = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Submission}}}"],
          "methodCalls":[["EmailSubmission/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",
            "sort":[{"property":"sentAt","isAscending":true}],
            "sinceQueryState":"{{{initialState}}}","maxChanges":0
          },"qc1"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("tooManyChanges", Arguments(limited)["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var createdChanges = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Submission}}}"],
          "methodCalls":[["EmailSubmission/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",
            "sort":[{"property":"sentAt","isAscending":true}],
            "sinceQueryState":"{{{initialState}}}","calculateTotal":true
          },"qc2"]]
        }
        """).ConfigureAwait(false);
        var createdArguments = Arguments(createdChanges);
        Assert.AreEqual(0, createdArguments["removed"]!.AsArray().Count);
        Assert.AreEqual(submissionObjectId, createdArguments["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, createdArguments["added"]![0]!["index"]!.GetValue<int>());
        Assert.AreEqual(1, createdArguments["total"]!.GetValue<int>());
        var createdState = createdArguments["newQueryState"]!.GetValue<string>();

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var submission = await database.JmapEmailSubmissions.SingleAsync().ConfigureAwait(false);
            submission.UpdatedAt = submission.UpdatedAt.AddSeconds(1);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        var unchangedQuery = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Submission}}}"],
          "methodCalls":[["EmailSubmission/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",
            "sort":[{"property":"sentAt","isAscending":true}],
            "sinceQueryState":"{{{createdState}}}"
          },"qc3"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(0, Arguments(unchangedQuery)["removed"]!.AsArray().Count);
        Assert.AreEqual(0, Arguments(unchangedQuery)["added"]!.AsArray().Count);
        var updatedState = Arguments(unchangedQuery)["newQueryState"]!.GetValue<string>();
        Assert.AreNotEqual(createdState, updatedState, StringComparer.Ordinal);

        await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Submission}}}"],
          "methodCalls":[["EmailSubmission/set",{
            "accountId":"{{{fixture.AccountId}}}","destroy":["{{{submissionObjectId}}}"]
          },"s1"]]
        }
        """).ConfigureAwait(false);
        var destroyedChanges = await fixture.InvokeAsync($$$"""
        {
          "using":["{{{Core}}}","{{{Submission}}}"],
          "methodCalls":[["EmailSubmission/queryChanges",{
            "accountId":"{{{fixture.AccountId}}}",
            "sort":[{"property":"sentAt","isAscending":true}],
            "sinceQueryState":"{{{updatedState}}}"
          },"qc4"]]
        }
        """).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { submissionObjectId },
            Arguments(destroyedChanges)["removed"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual(0, Arguments(destroyedChanges)["added"]!.AsArray().Count);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailCreationRejectsAmbiguousHeadersAndInvalidBodyParts scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailCreationRejectsAmbiguousHeadersAndInvalidBodyParts()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var existingBlob = await fixture.StoreBlobAsync([1, 2, 3], "application/octet-stream").ConfigureAwait(false);
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
              "malformedBlobId": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "attachments":[{
                  "blobId":"not valid", "type":"application/octet-stream"
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
              "duplicatePartId": {
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
                "bodyValues":{"same":{"value":"body"}},
                "bodyStructure":{
                  "type":"multipart/mixed",
                  "subParts":[
                    {"partId":"same", "type":"text/plain"},
                    {"partId":"same", "type":"text/html"}
                  ]
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
        """).ConfigureAwait(false);
        var failures = Arguments(response)["notCreated"]!.AsObject();
        Assert.AreEqual("invalidProperties", failures["wrongForm"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["duplicateRoot"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["partSize"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["partCharsetNull"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["emptyTextBody"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["nullTextBody"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["invalidBodyValue"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["nonTextCharset"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["invalidBlobSize"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["malformedBlobId"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.Contains(
            failures["malformedBlobId"]!["properties"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray(),
            "blobId");
        Assert.AreEqual("invalidProperties", failures["invalidMessageId"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["invalidParsedHeader"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["missingGroupedAddresses"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["nullGroupedAddresses"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["duplicateNullPartHeader"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", failures["duplicatePartId"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("blobNotFound", failures["allMissingBlobs"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            new[] { missingOne, missingTwo },
            failures["allMissingBlobs"]!["notFound"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray());
        Assert.IsNotNull(Arguments(response)["created"]!["nullableMetadata"]);
    }

    [TestMethod]
    public async Task EmailCreationPreservesMultipleInReplyToMessageIds()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
    public async Task EmailCreationPreservesMultipartNames()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"multipartName":{
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true},
              "bodyValues":{"1":{"value":"body"}},
              "bodyStructure":{
                "name":"related content",
                "type":"multipart/related",
                "subParts":[{"partId":"1", "type":"text/plain"}]
              }
            }}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["multipartName"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "ids":["{{{emailId}}}"],
            "properties":["bodyStructure"],
            "bodyProperties":["name", "type", "subParts"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);

        var bodyStructure = Arguments(get)["list"]![0]!["bodyStructure"]!;
        Assert.AreEqual("related content", bodyStructure["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("multipart/related", bodyStructure["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailCreationDefersInvalidSenderAndMessageIdCardinalityUntilSubmission scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailCreationDefersInvalidSenderAndMessageIdCardinalityUntilSubmission()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"draft":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "from":[{"email":"{{{fixture.User.Username}}}"}],
              "to":[{"email":"{{{fixture.User.Username}}}"}],
              "sender":[
                {"email":"first@example.test"},
                {"email":"second@example.test"}
              ],
              "messageId":["first@example.test", "second@example.test"],
              "bodyValues":{"1":{"value":"body"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["draft"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["sender", "messageId"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        var email = Arguments(get)["list"]![0]!;
        CollectionAssert.AreEqual(
            ExpectedVector8,
            email["sender"]!.AsArray()
                .Select(node => node!["email"]!.GetValue<string>())
                .ToArray());
        CollectionAssert.AreEqual(
            ExpectedVector8,
            email["messageId"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());

        var identities = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"]]
        }
        """).ConfigureAwait(false);
        var identityId = Arguments(identities)["list"]![0]!["id"]!.GetValue<string>();
        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"send":{"identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
              "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]
              }
            }}
          }, "e1"]]
        }
        """).ConfigureAwait(false);
        var error = Arguments(submission)["notCreated"]!["send"]!;
        Assert.AreEqual("invalidEmail", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            ExpectedVector9,
            error["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task EmailCreationPreservesExplicitEmptyConvenienceHeaders()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"draft":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "from":[], "sender":[], "to":[], "cc":[], "bcc":[], "replyTo":[],
              "messageId":[], "inReplyTo":[], "references":[],
              "bodyValues":{"1":{"value":"body"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["draft"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["headers"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        var names = Arguments(get)["list"]![0]!["headers"]!.AsArray()
            .Select(node => node!["name"]!.GetValue<string>())
            .ToArray();
        foreach (var name in new[]
                 {
                     "From", "Sender", "To", "Cc", "Bcc", "Reply-To",
                     "Message-ID", "In-Reply-To", "References",
                 })
        {
            Assert.AreEqual(1, names.Count(candidate => string.Equals(
                candidate,
                name,
                StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MimeHeadersSupportPermittedParsedForms scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MimeHeadersSupportPermittedParsedForms()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var email = Arguments(get)["list"]![0]!;
        StringAssert.StartsWith(
            email["header:Content-Type:asText"]!.GetValue<string>(),
            "text/plain", StringComparison.Ordinal);
        var headerNames = email["headers"]!.AsArray()
            .Select(header => header!["name"]!.GetValue<string>())
            .ToArray();
        CollectionAssert.Contains(headerNames, "Content-Type");
        CollectionAssert.Contains(headerNames, "Content-ID");
        Assert.AreEqual("part@example.test", email["bodyStructure"]!["cid"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            ExpectedVector10,
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
        """).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { emailId },
            Arguments(query)["ids"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailQueryUsesAllHeadersForTextAndLastProjectedHeaderForSort scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailQueryUsesAllHeadersForTextAndLastProjectedHeaderForSort()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var duplicateHeaders = Encoding.ASCII.GetBytes(
            "From: Zulu First <zulu-first@example.test>\r\n"
            + "From: Adam Last <adam-late@example.test>\r\n"
            + "To: Zulu First <zulu-to@example.test>\r\n"
            + "To: Adam Last <adam-to@example.test>\r\n"
            + "Subject: Zulu first subject\r\n"
            + "Subject: Alpha-last subject\r\n"
            + "Date: Sun, 20 Sep 2026 12:00:00 +0000\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Message-ID: <duplicate-query@example.test>\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n\r\nbody");
        var middleHeaders = Encoding.ASCII.GetBytes(
            "From: Middle Sender <middle@example.test>\r\n"
            + "To: Middle Recipient <middle-to@example.test>\r\n"
            + "Subject: Middle subject\r\n"
            + "Date: Sun, 20 Sep 2026 11:00:00 +0000\r\n"
            + "Message-ID: <middle-query@example.test>\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n\r\nbody");
        var duplicateBlobId = await fixture.StoreBlobAsync(duplicateHeaders).ConfigureAwait(false);
        var middleBlobId = await fixture.StoreBlobAsync(middleHeaders).ConfigureAwait(false);

        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{
              "duplicate":{"blobId":"{{{duplicateBlobId}}}",
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true}},
              "middle":{"blobId":"{{{middleBlobId}}}",
                "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true}}
            }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
        var duplicateId = Arguments(import)["created"]!["duplicate"]!["id"]!.GetValue<string>();
        var middleId = Arguments(import)["created"]!["middle"]!["id"]!.GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/get", {
              "accountId":"{{{fixture.AccountId}}}", "ids":["{{{duplicateId}}}"],
              "properties":["from", "to", "subject", "sentAt"]
            }, "g1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}", "filter":{"from":"adam-late"}
            }, "q1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}", "filter":{"text":"alpha-last"}
            }, "q2"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}", "sort":[{"property":"from"}]
            }, "q3"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}", "sort":[{"property":"to"}]
            }, "q4"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}", "sort":[{"property":"subject"}]
            }, "q5"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}", "sort":[{"property":"sentAt"}]
            }, "q6"]
          ]
        }
        """).ConfigureAwait(false);

        var projected = Arguments(response)["list"]![0]!;
        Assert.AreEqual("Adam Last", projected["from"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Adam Last", projected["to"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Alpha-last subject", projected["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("2026-09-20T10:00:00Z", projected["sentAt"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEqual(new[] { duplicateId }, QueryIds(response, 1));
        CollectionAssert.AreEqual(new[] { duplicateId }, QueryIds(response, 2));
        for (var index = 3; index <= 6; index++)
            CollectionAssert.AreEqual(new[] { duplicateId, middleId }, QueryIds(response, index));

        static string[] QueryIds(JsonObject value, int index) =>
            Arguments(value, index)["ids"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray();
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The EmailQueryUsesTheProjectedSizeForLegacyRows scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task EmailQueryUsesTheProjectedSizeForLegacyRows()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var legacyId = Guid.CreateVersion7();
        var currentId = Guid.CreateVersion7();
        var legacyRaw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Legacy size\r\n\r\n"
            + new string('x', 256));
        var currentRaw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Current size\r\n\r\nsmall");

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Emails.AddRangeAsync(
                new EmailDB
                {
                    Id = legacyId,
                    Sender = "sender@example.net",
                    Recipient = fixture.User.Username,
                    Subject = "Legacy size",
                    Body = new string('x', 256),
                    RawMessage = legacyRaw,
                    SizeBytes = 0,
                    ReceivedAt = DateTime.UtcNow.AddMinutes(-1),
                    FolderId = fixture.InboxFolderId,
                    Uid = 200,
                    ThreadObjectId = Guid.CreateVersion7().ToString("N"),
                },
                new EmailDB
                {
                    Id = currentId,
                    Sender = "sender@example.net",
                    Recipient = fixture.User.Username,
                    Subject = "Current size",
                    Body = "small",
                    RawMessage = currentRaw,
                    SizeBytes = currentRaw.Length,
                    ReceivedAt = DateTime.UtcNow,
                    FolderId = fixture.InboxFolderId,
                    Uid = 201,
                    ThreadObjectId = Guid.CreateVersion7().ToString("N"),
                }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "ids":["{{{JmapId.Email(legacyId)}}}"],
              "properties":["size"]
            }, "g1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"minSize":{{{legacyRaw.Length}}}}
            }, "q1"],
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "sort":[{"property":"size"}]
            }, "q2"]
          ]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual(
            legacyRaw.LongLength,
            Arguments(response)["list"]![0]!["size"]!.GetValue<long>());
        CollectionAssert.AreEqual(
            new[] { JmapId.Email(legacyId) },
            Arguments(response, 1)["ids"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { JmapId.Email(currentId), JmapId.Email(legacyId) },
            Arguments(response, 2)["ids"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task UploadBlobCanBeParsedAndImported()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.UTF8.GetBytes(
            "From: Sender <sender@example.net>\r\n"
            + "To: user@tenant.example.test\r\n"
            + "Subject: Imported message\r\n"
            + "Message-ID: <import-1@example.net>\r\n"
            + "Date: Sat, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\nImported body");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "Imported message",
            Arguments(parse)["parsed"]![blobId]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "Imported body",
            Arguments(parse)["parsed"]![blobId]!["bodyValues"]!["1"]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
        var importedId = Arguments(parse, 1)["created"]!["imp"]!["id"]!.GetValue<string>();

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{importedId}}}"],
            "properties":["id", "subject", "keywords"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("Imported message", Arguments(get)["list"]![0]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsTrue(Arguments(get)["list"]![0]!["keywords"]!["$seen"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ParsedBodyValuesOnlyNormalizeCrLfPairs()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n\r\n"
            + "first\rsecond\r\nthird\nfourth");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{blobId}}}"],
            "properties":["bodyValues"],
            "fetchAllBodyValues":true
          }, "p1"]]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual(
            "first\rsecond\nthird\nfourth",
            Arguments(response)["parsed"]![blobId]!["bodyValues"]!["1"]!["value"]!
                .GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ImportDefaultsReceivedAtFromMostRecentReceivedHeader()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "Received: from final.example by mx.example; Fri, 2 Jan 2026 03:04:05 EST\r\n"
            + "Received: from origin.example by final.example; Fri, 2 Jan 2026 12:00:00 +0000\r\n"
            + "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        var emailId = Arguments(import)["created"]!["received"]!["id"]!.GetValue<string>();
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["receivedAt"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "2026-01-02T08:04:05Z",
            Arguments(get)["list"]![0]!["receivedAt"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The HtmlSearchAndPreviewIgnoreNonRenderedContent scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task HtmlSearchAndPreviewIgnoreNonRenderedContent()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Subject: HTML search\r\nContent-Type: text/html; charset=utf-8\r\n\r\n"
            + "<html><head><title>private-head-token</title></head><body>"
            + "<style>.private-style-token { display: none }</style>"
            + "<script>private-script-token</script>"
            + "<p>Visible &amp; searchable</p></body></html>");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"html":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        Assert.AreEqual(0, Arguments(response)["total"]!.GetValue<int>());
        Assert.AreEqual(1, Arguments(response, 1)["total"]!.GetValue<int>());
        Assert.AreEqual(emailId, Arguments(response, 1)["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "Visible & searchable",
            Arguments(response, 2)["list"]![0]!["preview"]!.GetValue<string>(), StringComparer.Ordinal);
        var snippet = Arguments(response, 3)["list"]![0]!["preview"]!.GetValue<string>();
        StringAssert.Contains(snippet, "Visible &amp; <mark>searchable</mark>", StringComparison.Ordinal);
        Assert.IsFalse(snippet.Contains("private-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchIncludesEveryInlineTextBodyPart()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Subject: Multipart search\r\n"
            + "Content-Type: multipart/mixed; boundary=parts\r\n\r\n"
            + "--parts\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nOpening text\r\n"
            + "--parts\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nClosing needle\r\n"
            + "--parts--\r\n");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"multipart":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        Assert.AreEqual(emailId, Arguments(response)["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        StringAssert.Contains(
            Arguments(response, 1)["list"]![0]!["preview"]!.GetValue<string>(),
            "<mark>Closing</mark> <mark>needle</mark>", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SearchIncludesTextInsideAttachedMessages()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"attached":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        Assert.AreEqual(emailId, Arguments(response)["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        StringAssert.Contains(
            Arguments(response, 1)["list"]![0]!["preview"]!.GetValue<string>(),
            "<mark>Attached</mark> <mark>searchable</mark> <mark>needle</mark>", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SearchSnippetDecodesEscapedPhraseTerms()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Subject: Escaped phrase\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
            + "A say \"hello\" \\ path phrase");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"escaped-phrase":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(import)["created"]!["escaped-phrase"]!["id"]!
            .GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/query", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"\"say \\\"hello\\\" \\\\ path\""}
            }, "q1"],
            ["SearchSnippet/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"body":"\"say \\\"hello\\\" \\\\ path\""},
              "emailIds":["{{{emailId}}}"]
            }, "ss1"]
          ]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual(emailId, Arguments(response)["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        StringAssert.Contains(
            Arguments(response, 1)["list"]![0]!["preview"]!.GetValue<string>(),
            "<mark>say &quot;hello&quot; \\ path</mark>", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SearchSnippetPreservesPlainTextHtmlEntities()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Subject: Plain text entities\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
            + "Literal entity &lt;value&gt;");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"plain":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        Assert.AreEqual(
            "<mark>Literal</mark> entity &amp;lt;value&amp;gt;",
            Arguments(response)["list"]![0]!["preview"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task SearchSnippetDoesNotHighlightNegatedTerms()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var create = await CreateTextEmailAsync(
            fixture,
            "snippet-negation",
            "Allowed subject",
            "wanted blocked").ConfigureAwait(false);
        var emailId = Arguments(create)["created"]!["snippet-negation"]!["id"]!
            .GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["SearchSnippet/get", {
            "accountId":"{{{fixture.AccountId}}}",
            "filter":{"operator":"AND", "conditions":[
              {"body":"wanted"},
              {"operator":"NOT", "conditions":[{"subject":"blocked"}]}
            ]},
            "emailIds":["{{{emailId}}}"]
          }, "ss1"]]
        }
        """).ConfigureAwait(false);

        var preview = Arguments(response)["list"]![0]!["preview"]!.GetValue<string>();
        StringAssert.Contains(preview, "<mark>wanted</mark> blocked", StringComparison.Ordinal);
        Assert.IsFalse(preview.Contains("<mark>blocked</mark>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchSnippetUsesTheProjectedLastSubjectHeader()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "Subject: Superseded subject\r\n"
            + "Subject: Final needle subject\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"duplicate-subject":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(import)["created"]!["duplicate-subject"]!["id"]!
            .GetValue<string>();

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["Email/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "ids":["{{{emailId}}}"],
              "properties":["subject"]
            }, "g1"],
            ["SearchSnippet/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"subject":"needle"},
              "emailIds":["{{{emailId}}}"]
            }, "ss1"]
          ]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual(
            "Final needle subject",
            Arguments(response)["list"]![0]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "Final <mark>needle</mark> subject",
            Arguments(response, 1)["list"]![0]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task SearchSnippetFilterErrorsRemainBehindAccountAuthorization()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [
            ["SearchSnippet/get", {
              "accountId":"A00000000000000000000000000000000",
              "filter":{"unknownCondition":true},"emailIds":[]
            }, "missing"],
            ["SearchSnippet/get", {
              "accountId":"{{{fixture.AccountId}}}",
              "filter":{"unknownCondition":true},"emailIds":[]
            }, "unsupported"]
          ]
        }
        """).ConfigureAwait(false);

        Assert.AreEqual("accountNotFound", Arguments(response)["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("unsupportedFilter", Arguments(response, 1)["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void SearchSnippetGatewayKeepsOpaqueIdsAndRejectsUnexpectedWorkerRows()
    {
        var arguments = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111",
         "filter":{"operator":"AND","conditions":[
           {"text":"needle"},{"operator":"NOT","conditions":[{"body":"blocked"}]}]},
         "emailIds":["E22222222222222222222222222222222","opaque",
                     "E22222222222222222222222222222222"]}
        """)!.AsObject();
        Assert.IsTrue(GatewaySearchSnippetCodec.TryParse(arguments, 10, out var call, out var failure), failure);
        Assert.IsNotNull(call);
        CollectionAssert.AreEqual(ExpectedVector11, call.Command.Terms.ToArray());
        Assert.HasCount(1, call.Command.MessageIds);
        var result = new MailSearchSnippetResult(MailSearchSnippetStatus.Ok,
            [new(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "<needle>", "near needle")]);
        var rendered = GatewaySearchSnippetCodec.Render(call, result).Data;
        Assert.AreEqual("&lt;<mark>needle</mark>&gt;",
            rendered["list"]![0]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("opaque", rendered["notFound"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => GatewaySearchSnippetCodec.Render(call,
            new(MailSearchSnippetStatus.Ok,
                [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "needle", null)])));
        Assert.Throws<InvalidOperationException>(() => GatewaySearchSnippetCodec.Render(call,
            new(MailSearchSnippetStatus.AccountNotFound, result.Snippets)));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MimeProjectionPreservesNestedStructureAndResolvablePartBlobs scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MimeProjectionPreservesNestedStructureAndResolvablePartBlobs()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var relatedRaw = Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\n"
            + "To: user@tenant.example.test\r\n"
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
        var relatedBlobId = await fixture.StoreBlobAsync(relatedRaw).ConfigureAwait(false);
        var previewRaw = Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\nTo: user@tenant.example.test\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: 8bit\r\n\r\n"
            + string.Concat(Enumerable.Repeat("😀", 300)));
        var previewBlobId = await fixture.StoreBlobAsync(previewRaw).ConfigureAwait(false);
        var htmlPreviewBlobId = await fixture.StoreBlobAsync(Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\nTo: user@tenant.example.test\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n\r\n"
            + "<p>Hello &amp; welcome</p>")).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
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
                .GetValue<string>(), StringComparer.Ordinal);

        var attachedRaw = Encoding.UTF8.GetBytes(
            "From: sender@example.net\r\nTo: user@tenant.example.test\r\n"
            + "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=outer\r\n\r\n"
            + "--outer\r\nContent-Type: text/plain\r\n\r\nOuter payload\r\n"
            + "--outer\r\nContent-Type: message/rfc822\r\n"
            + "Content-Disposition: attachment; filename=nested.eml\r\n\r\n"
            + "From: nested@example.net\r\nTo: user@tenant.example.test\r\n"
            + "Date: Sun, 20 Sep 2026 11:00:00 +0000\r\n"
            + "Subject: Attached\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
            + "Nested payload\r\n--outer--\r\n");
        var attachedBlobId = await fixture.StoreBlobAsync(attachedRaw).ConfigureAwait(false);
        var outerParse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}", "blobIds":["{{{attachedBlobId}}}"],
            "properties":["attachments"], "bodyProperties":["blobId", "type"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var nestedBodyBlobId = Arguments(innerParse)["parsed"]![messageBlobId]!["textBody"]![0]!["blobId"]!
            .GetValue<string>();
        using var scope = fixture.Services.CreateScope();
        var nestedBody = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, nestedBodyBlobId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(nestedBody);
        Assert.AreEqual("Nested payload", Encoding.UTF8.GetString(nestedBody.Content).Trim(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task DeepMimePartBlobIdsRemainResolvableWithinTheIdLimit()
    {
        const int depth = 70;
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var uploadedBlobId = await fixture.StoreBlobAsync(
            BuildDeepMultipartMessage(fixture.User.Username, depth, "Deep payload")).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{uploadedBlobId}}}"],
            "properties":["textBody"],
            "bodyProperties":["partId", "blobId"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);

        var part = Arguments(response)["parsed"]![uploadedBlobId]!["textBody"]![0]!;
        var partId = part["partId"]!.GetValue<string>();
        var partBlobId = part["blobId"]!.GetValue<string>();
        Assert.IsTrue(partId.Length > 128);
        Assert.IsTrue(JmapId.IsValidId(partBlobId));
        using var scope = fixture.Services.CreateScope();
        var content = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, partBlobId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(content);
        Assert.AreEqual("Deep payload", Encoding.ASCII.GetString(content.Content).Trim(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task VeryDeepMimePartBlobIdsAreBoundedAndResolvable()
    {
        const int depth = 85;
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var uploadedBlobId = await fixture.StoreBlobAsync(
            BuildDeepMultipartMessage(fixture.User.Username, depth, "Very deep payload")).ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{uploadedBlobId}}}"],
            "properties":["textBody"],
            "bodyProperties":["partId", "blobId"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);

        var part = Arguments(response)["parsed"]![uploadedBlobId]!["textBody"]![0]!;
        var partId = part["partId"]!.GetValue<string>();
        var partBlobId = part["blobId"]!.GetValue<string>();
        Assert.IsTrue(partId.Length > 165);
        Assert.IsTrue(JmapId.IsValidId(partBlobId));
        Assert.IsTrue(JmapId.TryParseHashedBodyPartBlob(
            partBlobId,
            out _,
            out var nestingDepth,
            out _));
        Assert.AreEqual(0, nestingDepth);
        using var scope = fixture.Services.CreateScope();
        var content = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, partBlobId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(content);
        Assert.AreEqual("Very deep payload", Encoding.ASCII.GetString(content.Content).Trim(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task HashedNestedMimePartBlobIdsPreserveTheirSourcePath()
    {
        const int depth = 85;
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var nested = BuildDeepMultipartMessage(
            fixture.User.Username,
            depth,
            "Nested very deep payload");
        var outer = Encoding.ASCII.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: message/rfc822\r\n"
            + "Content-Disposition: attachment; filename=nested.eml\r\n"
            + "Content-Transfer-Encoding: base64\r\n\r\n"
            + Convert.ToBase64String(nested)
            + "\r\n");
        var outerBlobId = await fixture.StoreBlobAsync(outer).ConfigureAwait(false);
        var outerParse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{outerBlobId}}}"],
            "properties":["bodyStructure"],
            "bodyProperties":["blobId"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
        var messageBlobId = Arguments(outerParse)["parsed"]![outerBlobId]!["bodyStructure"]!["blobId"]!
            .GetValue<string>();
        var innerParse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}",
            "blobIds":["{{{messageBlobId}}}"],
            "properties":["textBody"],
            "bodyProperties":["blobId"]
          }, "p2"]]
        }
        """).ConfigureAwait(false);
        var nestedBodyBlobId = Arguments(innerParse)["parsed"]![messageBlobId]!["textBody"]![0]!["blobId"]!
            .GetValue<string>();
        Assert.IsTrue(JmapId.IsValidId(nestedBodyBlobId));
        Assert.IsTrue(JmapId.TryParseHashedBodyPartBlob(
            nestedBodyBlobId,
            out _,
            out var nestingDepth,
            out _));
        Assert.AreEqual(1, nestingDepth);
        using var scope = fixture.Services.CreateScope();
        var content = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, nestedBodyBlobId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(content);
        Assert.AreEqual(
            "Nested very deep payload",
            Encoding.ASCII.GetString(content.Content).Trim(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task MimeProjectionPreservesSequentialBodyOrderOutsideAlternatives()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            $"From: sender@example.net\r\nTo: {fixture.User.Username}\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=body\r\n\r\n"
            + "--body\r\nContent-Type: text/plain\r\n\r\nFirst\r\n"
            + "--body\r\nContent-Type: image/png\r\nContent-Disposition: inline\r\n\r\npng\r\n"
            + "--body\r\nContent-Type: text/html\r\n\r\n<p>Last</p>\r\n"
            + "--body--\r\n");
        var blobId = await fixture.StoreBlobAsync(raw).ConfigureAwait(false);

        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/parse", {
            "accountId":"{{{fixture.AccountId}}}", "blobIds":["{{{blobId}}}"],
            "properties":["textBody", "htmlBody", "attachments"],
            "bodyProperties":["type"]
          }, "p1"]]
        }
        """).ConfigureAwait(false);
        var email = Arguments(response)["parsed"]![blobId]!;
        var expected = new[] { "text/plain", "image/png", "text/html" };
        CollectionAssert.AreEqual(
            expected,
            email["textBody"]!.AsArray()
                .Select(node => node!["type"]!.GetValue<string>())
                .ToArray());
        CollectionAssert.AreEqual(
            expected,
            email["htmlBody"]!.AsArray()
                .Select(node => node!["type"]!.GetValue<string>())
                .ToArray());
        Assert.AreEqual(0, email["attachments"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task AttachedMessageBlobPreservesExactDecodedOctets()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        var outerBlobId = await fixture.StoreBlobAsync(outerRaw).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        var attachment = Arguments(parse)["parsed"]![outerBlobId]!["attachments"]![0]!;
        Assert.AreEqual("message/rfc822", attachment["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(nestedRaw.Length, attachment["size"]!.GetValue<int>());

        using var scope = fixture.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(
                fixture.InboxId,
                attachment["blobId"]!.GetValue<string>(),
                CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(nestedRaw, stored.Content);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The BodyLanguageUsesRfcLanguageTags scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task BodyLanguageUsesRfcLanguageTags()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var validBlobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n"
            + "Content-Language: en-US (primary), i-klingon\r\n\r\nbody")).ConfigureAwait(false);
        var invalidBlobId = await fixture.StoreBlobAsync(Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n"
            + "Content-Language: en_US\r\n\r\nbody")).ConfigureAwait(false);

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
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "en-US|i-klingon",
            string.Join(
                '|',
                Arguments(parse)["parsed"]![validBlobId]!["bodyStructure"]!["language"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())), StringComparer.Ordinal);
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
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(create)["notCreated"]!["badLanguage"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
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
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "en-US|i-klingon",
            string.Join(
                '|',
                Arguments(get)["list"]![0]!["bodyStructure"]!["language"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task UploadedBlobPersistsOnlyAzureCompatibleObjectReference()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var content = "large-object-boundary"u8.ToArray();
        var blobId = await fixture.StoreBlobAsync(content, "application/octet-stream").ConfigureAwait(false);

        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var row = await database.JmapBlobs.AsNoTracking()
            .SingleAsync(candidate => candidate.BlobId == blobId).ConfigureAwait(false);
        Assert.IsNull(row.Content);
        Assert.AreEqual("azure-blob", row.ObjectProvider, StringComparer.Ordinal);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.ObjectName));
        Assert.AreEqual(64, row.ObjectSha256?.Length);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.ObjectEntityTag));
        Assert.AreEqual(content.LongLength, row.SizeBytes);

        var loaded = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, blobId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        CollectionAssert.AreEqual(content, loaded.Content);
    }

    [TestMethod]
    public async Task LegacyInlineBlobMigratesIdempotentlyToAzureCompatibleObjectReference()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var content = "legacy-inline-blob"u8.ToArray();
        var id = Guid.CreateVersion7();
        var blobId = JmapId.UploadedBlob(id);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.JmapBlobs.AddAsync(new JmapBlobDB
        {
            Id = id,
            BlobId = blobId,
            AccountId = fixture.InboxId,
            ContentType = "application/octet-stream",
            Content = content,
            SizeBytes = content.LongLength,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var migration = scope.ServiceProvider
            .GetRequiredService<JmapBlobLargeObjectMigrationService>();
        await migration.MigrateAsync().ConfigureAwait(false);
        await migration.MigrateAsync().ConfigureAwait(false);
        database.ChangeTracker.Clear();

        var migrated = await database.JmapBlobs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == id).ConfigureAwait(false);
        Assert.IsNull(migrated.Content);
        Assert.AreEqual("azure-blob", migrated.ObjectProvider, StringComparer.Ordinal);
        Assert.AreEqual($"jmap/uploads/{fixture.InboxId:N}/{id:N}", migrated.ObjectName, StringComparer.Ordinal);
        Assert.AreEqual(64, migrated.ObjectSha256?.Length);
        Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.ObjectEntityTag));
        Assert.AreEqual(1, scope.ServiceProvider
            .GetRequiredService<InMemoryLargeObjectStore>().Count);

        var loaded = await scope.ServiceProvider.GetRequiredService<JmapBlobService>()
            .GetAsync(fixture.InboxId, blobId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        CollectionAssert.AreEqual(content, loaded.Content);
    }

    [TestMethod]
    public async Task BlobQuotaEvictsOldestUnreferencedUpload()
    {
        const int quota = 1_048_576;
        var fixture = (await JmapFixture.CreateAsync(quota).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var first = await fixture.StoreBlobAsync(new byte[700_000], "application/octet-stream").ConfigureAwait(false);
        var second = await fixture.StoreBlobAsync(new byte[700_000], "application/octet-stream").ConfigureAwait(false);

        using var scope = fixture.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<JmapBlobService>();
        Assert.IsNull(await blobs.GetAsync(fixture.InboxId, first, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await blobs.GetAsync(fixture.InboxId, second, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1, scope.ServiceProvider
            .GetRequiredService<InMemoryLargeObjectStore>().Count);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The IdentitySubmissionQueuesMessageAndStripsBcc scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task IdentitySubmissionQueuesMessageAndStripsBcc()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var identityResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var createdEmail = Arguments(emailResponse)["created"]!["send"]!;
        var emailId = createdEmail["id"]!.GetValue<string>();
        var emailSize = createdEmail["size"]!.GetValue<int>();
        var oversizedMailFrom = new string('a', 65) + "@example.net";

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
              },
              "badRecipientShape":{
                "identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
                "envelope":{
                  "mailFrom":{"email":"{{{fixture.User.Username}}}"},
                  "rcptTo":[{"parameters":null}]
                }
              },
              "badRecipientAddress":{
                "identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
                "envelope":{
                  "mailFrom":{"email":"{{{fixture.User.Username}}}"},
                  "rcptTo":[{"email":"not an address"}]
                }
              },
              "oversizedMailFrom":{
                "identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
                "envelope":{
                  "mailFrom":{"email":"{{{oversizedMailFrom}}}"},
                  "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]
                }
              }
            }
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var createdSubmission = Arguments(submissionResponse)["created"]!["submit"]!.AsObject();
        var submissionId = createdSubmission["id"]!.GetValue<string>();
        CollectionAssert.AreEquivalent(
            ExpectedVector12,
            createdSubmission.Select(property => property.Key).ToArray());
        Assert.AreEqual("final", createdSubmission["undoStatus"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, createdSubmission["dsnBlobIds"]!.AsArray().Count);
        Assert.AreEqual(0, createdSubmission["mdnBlobIds"]!.AsArray().Count);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(submissionResponse)["notCreated"]!["badParameter"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(submissionResponse)["notCreated"]!["badRecipientParameter"]!["type"]!
                .GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(submissionResponse)["notCreated"]!["badRecipientShape"]!["type"]!
                .GetValue<string>(), StringComparer.Ordinal);
        var invalidRecipient = Arguments(submissionResponse)["notCreated"]!["badRecipientAddress"]!;
        Assert.AreEqual("invalidRecipients", invalidRecipient["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "not an address",
            invalidRecipient["invalidRecipients"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(submissionResponse)["notCreated"]!["oversizedMailFrom"]!["type"]!
                .GetValue<string>(), StringComparer.Ordinal);

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queueContent = scope.ServiceProvider.GetRequiredService<MailQueueContentService>();
            var queued = await database.MailQueueMessages.Include(message => message.Recipients).SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(fixture.User.Username, queued.EnvelopeSender, StringComparer.Ordinal);
            Assert.AreEqual(fixture.User.Username, queued.Recipients.Single().Recipient, StringComparer.Ordinal);
            Assert.IsNull(queued.RawMessage);
            Assert.IsFalse((await queueContent.ReadAsync(queued).ConfigureAwait(false))
                .Contains("Bcc:", StringComparison.OrdinalIgnoreCase));
        }

        var read = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{submissionId}}}"]
          }, "s2"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("final", Arguments(read)["list"]![0]!["undoStatus"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(emailId, Arguments(read)["list"]![0]!["emailId"]!.GetValue<string>(), StringComparer.Ordinal);
        var storedSubmission = Arguments(read)["list"]![0]!.AsObject();
        Assert.AreEqual(
            emailSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            storedSubmission["envelope"]!["mailFrom"]!["parameters"]!["SIZE"]!
                .GetValue<string>(), StringComparer.Ordinal);
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
        }).ConfigureAwait(false);
        Assert.IsNull(Arguments(roundTrip)["notUpdated"]);
        Assert.IsTrue(Arguments(roundTrip)["updated"]!.AsObject().ContainsKey(submissionId));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SubmissionCreationReferencesDoNotEchoClientPropertiesAndRunImplicitEmailSet scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SubmissionCreationReferencesDoNotEchoClientPropertiesAndRunImplicitEmailSet()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var identityResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"]]
        }
        """).ConfigureAwait(false);
        var identityId = Arguments(identityResponse)["list"]![0]!["id"]!.GetValue<string>();
        var emailResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/set", {
            "accountId":"{{{fixture.AccountId}}}", "create":{"draft":{
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true},
              "keywords":{"$draft":true},
              "from":[{"email":"{{{fixture.User.Username}}}"}],
              "to":[{"email":"{{{fixture.User.Username}}}"}],
              "subject":"Creation references",
              "bodyValues":{"1":{"value":"queued"}},
              "textBody":[{"partId":"1", "type":"text/plain"}]
            }}
          }, "e1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(emailResponse)["created"]!["draft"]!["id"]!.GetValue<string>();

        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}", "{{{Submission}}}"],
          "createdIds": {"identity":"{{{identityId}}}", "email":"{{{emailId}}}"},
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"submit":{"identityId":"#identity", "emailId":"#email"}},
            "onSuccessUpdateEmail":{"#submit":{"keywords/$draft":null}}
          }, "s1"]]
        }
        """).ConfigureAwait(false);

        var responses = submission["methodResponses"]!.AsArray();
        Assert.AreEqual(2, responses.Count);
        Assert.AreEqual("EmailSubmission/set", responses[0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Email/set", responses[1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("s1", responses[0]![2]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("s1", responses[1]![2]!.GetValue<string>(), StringComparer.Ordinal);
        var createdSubmission = Arguments(submission)["created"]!["submit"]!.AsObject();
        CollectionAssert.DoesNotContain(createdSubmission.Select(property => property.Key).ToArray(), "identityId");
        CollectionAssert.DoesNotContain(createdSubmission.Select(property => property.Key).ToArray(), "emailId");
        Assert.IsNotNull(createdSubmission["envelope"]);
        Assert.IsTrue(Arguments(submission, 1)["updated"]!.AsObject().ContainsKey(emailId));

        var email = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{emailId}}}"],
            "properties":["keywords"]
          }, "g1"]]
        }
        """).ConfigureAwait(false);
        Assert.IsFalse(Arguments(email)["list"]![0]!["keywords"]!.AsObject().ContainsKey("$draft"));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SubmissionGeneratesAndPersistsDeduplicatedEnvelope scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SubmissionGeneratesAndPersistsDeduplicatedEnvelope()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var identityResponse = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"]]
        }
        """).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var emailId = Arguments(emailResponse)["created"]!["generated"]!["id"]!.GetValue<string>();
        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"generated":{"identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}"}}
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        var createdSubmission = Arguments(submission)["created"]!["generated"]!.AsObject();
        var submissionId = createdSubmission["id"]!.GetValue<string>();
        CollectionAssert.AreEquivalent(
            ExpectedVector13,
            createdSubmission.Select(property => property.Key).ToArray());
        var read = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["{{{submissionId}}}"],
            "properties":["id", "envelope"]
          }, "s2"]]
        }
        """).ConfigureAwait(false);
        var envelope = Arguments(read)["list"]![0]!["envelope"]!;
        Assert.IsTrue(JsonNode.DeepEquals(createdSubmission["envelope"], envelope));
        Assert.AreEqual(fixture.User.Username, envelope["mailFrom"]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNull(envelope["mailFrom"]!["parameters"]);
        Assert.AreEqual(1, envelope["rcptTo"]!.AsArray().Count);
        Assert.AreEqual(fixture.User.Username, envelope["rcptTo"]![0]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNull(envelope["rcptTo"]![0]!["parameters"]);
    }

    [TestMethod]
    public async Task IdentitySetReturnsOnlyServerSetAndDefaultedProperties()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create": {
              "explicit": {
                "email":"{{{fixture.User.Username}}}",
                "name":"Explicit",
                "replyTo":null,
                "bcc":null,
                "textSignature":"text",
                "htmlSignature":"<b>html</b>"
              },
              "defaults": {"email":"{{{fixture.User.Username}}}"}
            }
          }, "s1"]]
        }
        """).ConfigureAwait(false);

        var created = Arguments(response)["created"]!.AsObject();
        CollectionAssert.AreEquivalent(
            ExpectedVector14,
            created["explicit"]!.AsObject().Select(property => property.Key).ToArray());
        CollectionAssert.AreEquivalent(
            ExpectedVector15,
            created["defaults"]!.AsObject().Select(property => property.Key).ToArray());
        Assert.AreEqual(string.Empty, created["defaults"]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNull(created["defaults"]!["replyTo"]);
        Assert.IsNull(created["defaults"]!["bcc"]);
        Assert.AreEqual(string.Empty, created["defaults"]!["textSignature"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(string.Empty, created["defaults"]!["htmlSignature"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The IdentitySetKeepsReferencesPatchesOwnershipAndDeletionAtomic scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task IdentitySetKeepsReferencesPatchesOwnershipAndDeletionAtomic()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var created = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{
              "fresh":{"email":"{{{fixture.User.Username}}}","name":"Before"},
              "transient":{"email":"{{{fixture.User.Username}}}"},
              "foreign":{"email":"foreign@example.net"},
              "badAddress":{"email":"{{{fixture.User.Username}}}",
                "replyTo":[{"email":"User <{{{fixture.User.Username}}}>"}]}
            },
            "update":{"#fresh":{"name":"After","textSignature":null},
              "#transient":{"name":"will not apply"}},
            "destroy":["#transient"]
          }, "set"]]
        }
        """).ConfigureAwait(false);
        var wireId = Arguments(created)["created"]!["fresh"]!["id"]!.GetValue<string>();
        var transientId = Arguments(created)["created"]!["transient"]!["id"]!.GetValue<string>();
        Assert.AreEqual("forbiddenFrom", Arguments(created)["notCreated"]!["foreign"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", Arguments(created)["notCreated"]!["badAddress"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsTrue(Arguments(created)["updated"]!.AsObject().ContainsKey(wireId));
        Assert.AreEqual("willDestroy", Arguments(created)["notUpdated"]!["#transient"]!["type"]!
            .GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(transientId, Arguments(created)["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);

        var checkedPatch = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{
              "{{{wireId}}}":{"id":"{{{wireId}}}","email":"{{{fixture.User.Username}}}",
                "mayDelete":true,"replyTo":[{"email":"reply@example.net","name":null}]}
            }
          }, "patch"]]
        }
        """).ConfigureAwait(false);
        Assert.IsNull(Arguments(checkedPatch)["notUpdated"]);
        Assert.IsTrue(Arguments(checkedPatch)["updated"]!.AsObject().ContainsKey(wireId));

        var read = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {
            "accountId":"{{{fixture.AccountId}}}","ids":["{{{wireId}}}"]
          }, "read"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("After", Arguments(read)["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("reply@example.net", Arguments(read)["list"]![0]!["replyTo"]![0]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(string.Empty, Arguments(read)["list"]![0]!["textSignature"]!.GetValue<string>(), StringComparer.Ordinal);

        var invalid = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [
            ["Identity/set",{"accountId":"{{{fixture.AccountId}}}",
              "update":{"{{{wireId}}}":{"email":"changed@example.net"} } },"immutable"],
            ["Identity/set",{"accountId":"{{{fixture.AccountId}}}",
              "update":{"{{{wireId}}}":{"name/child":"broken"} } },"patch"]
          ]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("invalidProperties", Arguments(invalid)["notUpdated"]![wireId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("email", Arguments(invalid)["notUpdated"]![wireId]!["properties"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidPatch", Arguments(invalid, 1)["notUpdated"]![wireId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var destroyed = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"{{{wireId}}}":{"name":"will not apply"},
              "I22222222222222222222222222222222":{"name":"missing"}},
            "destroy":["{{{wireId}}}","I{{{fixture.InboxId:N}}}",
              "I22222222222222222222222222222222"]
          }, "destroy"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("willDestroy", Arguments(destroyed)["notUpdated"]![wireId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("notFound", Arguments(destroyed)["notUpdated"]!["I22222222222222222222222222222222"]!["type"]!
            .GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(wireId, Arguments(destroyed)["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("forbidden", Arguments(destroyed)["notDestroyed"]![$"I{fixture.InboxId:N}"]!["type"]!
            .GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("notFound", Arguments(destroyed)["notDestroyed"]!["I22222222222222222222222222222222"]!["type"]!
            .GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The IdentityAndSubmissionRejectInvalidWireValues scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task IdentityAndSubmissionRejectInvalidWireValues()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        var identityId = Arguments(identities)["list"]![0]!["id"]!.GetValue<string>();
        var identityFailures = Arguments(identities, 1)["notCreated"]!.AsObject();
        Assert.AreEqual("invalidProperties", identityFailures["nullName"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", identityFailures["nullSignature"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidProperties", identityFailures["displayAddress"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        var malformedRaw = Encoding.UTF8.GetBytes(
            "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + "Date: Sun, 20 Sep 2026 11:00:00 +0000\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + "To: local-only\r\n"
            + "Subject: Invalid singleton headers\r\n\r\nbody");
        var blobId = await fixture.StoreBlobAsync(malformedRaw).ConfigureAwait(false);
        var malformedIds = Encoding.UTF8.GetBytes(
            "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Message-ID: missing-brackets@example.com\r\n"
            + "In-Reply-To: <parent@example.com> trailing-junk\r\n"
            + "References: (comment only)\r\n"
            + "Subject: Invalid message ids\r\n\r\nbody");
        var malformedIdsBlobId = await fixture.StoreBlobAsync(malformedIds).ConfigureAwait(false);
        var malformedDates = Encoding.UTF8.GetBytes(
            "Date: Mon, 20 Sep 2026 10:00:00 +0000\r\n"
            + $"Resent-From: {fixture.User.Username}\r\n"
            + "Resent-Date: 20 Sep 26 10:00:00 GMT\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Invalid dates\r\n\r\nbody");
        var malformedDatesBlobId = await fixture.StoreBlobAsync(malformedDates).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"bad":{"blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true} },
              "badIds":{"blobId":"{{{malformedIdsBlobId}}}",
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true} },
              "badDates":{"blobId":"{{{malformedDatesBlobId}}}",
              "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true} } }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
        var emailId = Arguments(import)["created"]!["bad"]!["id"]!.GetValue<string>();
        var malformedIdsEmailId = Arguments(import)["created"]!["badIds"]!["id"]!.GetValue<string>();
        var malformedDatesEmailId = Arguments(import)["created"]!["badDates"]!["id"]!.GetValue<string>();
        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{"bad":{"identityId":"{{{identityId}}}", "emailId":"{{{emailId}}}",
              "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]} },
              "badIds":{"identityId":"{{{identityId}}}", "emailId":"{{{malformedIdsEmailId}}}",
              "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]} },
              "badDates":{"identityId":"{{{identityId}}}", "emailId":"{{{malformedDatesEmailId}}}",
              "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]} } }
          }, "s2"]]
        }
        """).ConfigureAwait(false);
        var error = Arguments(submission)["notCreated"]!["bad"]!;
        Assert.AreEqual("invalidEmail", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            ExpectedVector16,
            error["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        var idError = Arguments(submission)["notCreated"]!["badIds"]!;
        Assert.AreEqual("invalidEmail", idError["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            ExpectedVector17,
            idError["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        var dateError = Arguments(submission)["notCreated"]!["badDates"]!;
        Assert.AreEqual("invalidEmail", dateError["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            ExpectedVector18,
            dateError["properties"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SubmissionValidatesAddressListsAndResentBlocks scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SubmissionValidatesAddressListsAndResentBlocks()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var identities = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "g1"]]
        }
        """).ConfigureAwait(false);
        var identityId = Arguments(identities)["list"]![0]!["id"]!.GetValue<string>();

        var validRaw = Encoding.UTF8.GetBytes(
            $"Resent-From: {fixture.User.Username}\r\n"
            + "Resent-To: first@example.test\r\n"
            + "Resent-Date: 19 Sep 2026 10:00:00 +0000\r\n"
            + "Resent-Message-ID: <first@example.test>\r\n"
            + $"Resent-From: {fixture.User.Username}, delegate@example.test\r\n"
            + $"Resent-Sender: {fixture.User.Username}\r\n"
            + "Resent-Cc: Undisclosed:;\r\n"
            + "Resent-Bcc: \r\n"
            + "Resent-Date: 18 Sep 2026 10:00:00 +0000\r\n"
            + "Resent-Message-ID: <second@example.test>\r\n"
            + "Date: 20 Sep 2026 10:00:00 +0000\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Bcc: (undisclosed recipients)\r\n"
            + "Subject: Valid resent blocks\r\n\r\nbody");
        var invalidRaw = Encoding.UTF8.GetBytes(
            $"Resent-From: {fixture.User.Username}, delegate@example.test\r\n"
            + "Resent-To: \r\n"
            + "Resent-Cc: local-only\r\n"
            + "Resent-Bcc: (undisclosed recipients)\r\n"
            + "Resent-Message-ID: missing-brackets@example.test\r\n"
            + $"Resent-Reply-To: {fixture.User.Username}\r\n"
            + "Date: 20 Sep 2026 10:00:00 +0000\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + "Reply-To: \r\n"
            + "To: \r\n"
            + "Cc: \r\n"
            + "Bcc: (undisclosed recipients)\r\n"
            + "Subject: Invalid resent block\r\n\r\nbody");
        var validBlobId = await fixture.StoreBlobAsync(validRaw).ConfigureAwait(false);
        var invalidBlobId = await fixture.StoreBlobAsync(invalidRaw).ConfigureAwait(false);
        var import = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{
              "valid":{"blobId":"{{{validBlobId}}}",
                "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true}},
              "invalid":{"blobId":"{{{invalidBlobId}}}",
                "mailboxIds":{"{{{fixture.DraftsMailboxId}}}":true}}
            }
          }, "i1"]]
        }
        """).ConfigureAwait(false);
        var validEmailId = Arguments(import)["created"]!["valid"]!["id"]!.GetValue<string>();
        var invalidEmailId = Arguments(import)["created"]!["invalid"]!["id"]!.GetValue<string>();

        var submission = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["EmailSubmission/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "create":{
              "valid":{"identityId":"{{{identityId}}}", "emailId":"{{{validEmailId}}}",
                "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                  "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]}},
              "invalid":{"identityId":"{{{identityId}}}", "emailId":"{{{invalidEmailId}}}",
                "envelope":{"mailFrom":{"email":"{{{fixture.User.Username}}}"},
                  "rcptTo":[{"email":"{{{fixture.User.Username}}}"}]}}
            }
          }, "s1"]]
        }
        """).ConfigureAwait(false);
        Assert.IsNotNull(Arguments(submission)["created"]!["valid"]);
        var error = Arguments(submission)["notCreated"]!["invalid"]!;
        Assert.AreEqual("invalidEmail", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            ExpectedVector19,
            error["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SubmissionEnforcesInternetMessageWireLimits scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SubmissionEnforcesInternetMessageWireLimits()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var identities = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "g1"]]
        }
        """).ConfigureAwait(false);
        var identityId = Arguments(identities)["list"]![0]!["id"]!.GetValue<string>();

        string Message(string extraHeader, string body, string newline = "\r\n") =>
            "Date: 31 Dec 2016 23:59:60 +0000" + newline
            + $"From: {fixture.User.Username}" + newline
            + $"To: {fixture.User.Username}" + newline
            + extraHeader + newline + newline + body;

        var invalidUtf8Raw = Encoding.ASCII.GetBytes(Message("Subject: x", string.Empty));
        invalidUtf8Raw[Array.LastIndexOf(invalidUtf8Raw, (byte)'x')] = 0xc3;

        var rawMessages = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["valid"] = Encoding.ASCII.GetBytes(
                Message("X-Max: " + new string('x', 991), new string('b', 998))),
            ["validFoldedDate"] = Encoding.ASCII.GetBytes(
                "Date:\r\n 31 Dec 2016 23:59:60 +0000\r\n"
                + $"From: {fixture.User.Username}\r\n"
                + $"To: {fixture.User.Username}\r\n\r\nbody"),
            ["longHeader"] = Encoding.ASCII.GetBytes(
                Message("X-Max: " + new string('x', 992), "body")),
            ["longBody"] = Encoding.ASCII.GetBytes(
                Message("Subject: Long body", new string('b', 999))),
            ["lfOnly"] = Encoding.ASCII.GetBytes(
                Message("Subject: LF only", "body", "\n")),
            ["invalidUtf8"] = invalidUtf8Raw,
        };
        var imports = new JsonObject();
        foreach (var item in rawMessages)
        {
            imports[item.Key] = new JsonObject
            {
                ["blobId"] = await fixture.StoreBlobAsync(item.Value).ConfigureAwait(false),
                ["mailboxIds"] = new JsonObject { [fixture.DraftsMailboxId] = true },
            };
        }
        var import = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Mail),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "Email/import",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["emails"] = imports,
                },
                "i1")),
        }).ConfigureAwait(false);
        var imported = Arguments(import)["created"]!.AsObject();
        var submissions = new JsonObject();
        foreach (var name in rawMessages.Keys)
        {
            submissions[name] = new JsonObject
            {
                ["identityId"] = identityId,
                ["emailId"] = imported[name]!["id"]!.GetValue<string>(),
                ["envelope"] = new JsonObject
                {
                    ["mailFrom"] = new JsonObject { ["email"] = fixture.User.Username },
                    ["rcptTo"] = new JsonArray(
                        new JsonObject { ["email"] = fixture.User.Username }),
                },
            };
        }
        var submission = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Submission),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "EmailSubmission/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["create"] = submissions,
                },
                "s1")),
        }).ConfigureAwait(false);

        Assert.IsNotNull(Arguments(submission)["created"]!["valid"]);
        Assert.IsNotNull(Arguments(submission)["created"]!["validFoldedDate"]);
        AssertInvalid("longHeader", "headers");
        AssertInvalid("longBody", "bodyStructure");
        AssertInvalid("lfOnly", "headers");
        AssertInvalid("invalidUtf8", "headers");

        void AssertInvalid(string name, string property)
        {
            var error = Arguments(submission)["notCreated"]![name]!;
            Assert.AreEqual("invalidEmail", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
            CollectionAssert.Contains(
                error["properties"]!.AsArray()
                    .Select(node => node!.GetValue<string>())
                    .ToArray(),
                property);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SubmissionEnforcesTheRawSizeForLegacyRows scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SubmissionEnforcesTheRawSizeForLegacyRows()
    {
        const int maximumMessageSize = 512;
        var fixture = (await JmapFixture.CreateAsync(maximumMessageSize).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var raw = Encoding.ASCII.GetBytes(
            "Date: Sun, 20 Sep 2026 10:00:00 +0000\r\n"
            + $"From: {fixture.User.Username}\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Oversized legacy row\r\n\r\n"
            + string.Join("\r\n", Enumerable.Repeat(new string('x', 100), 6)));
        Assert.IsTrue(raw.Length > maximumMessageSize);

        var emailId = Guid.CreateVersion7();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var drafts = await database.Folders.SingleAsync(folder =>
                folder.Id == fixture.DraftsFolderId).ConfigureAwait(false);
            await database.Emails.AddAsync(new EmailDB
            {
                Id = emailId,
                Sender = fixture.User.Username,
                Recipient = fixture.User.Username,
                Subject = "Oversized legacy row",
                Body = Encoding.ASCII.GetString(raw),
                RawMessage = raw,
                SizeBytes = 0,
                MessageId = $"<{emailId:N}@tenant.example.test>",
                EmailObjectId = emailId.ToString("N"),
                ThreadObjectId = emailId.ToString("N"),
                ReceivedAt = DateTime.UtcNow,
                FolderId = drafts.Id,
                Uid = drafts.NextUid++,
                ModSeq = ++drafts.HighestModSeq,
                IsDraft = true,
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var identities = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}"],
          "methodCalls": [["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "g1"]]
        }
        """).ConfigureAwait(false);
        var identityId = Arguments(identities)["list"]![0]!["id"]!.GetValue<string>();
        var submission = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core, Submission),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "EmailSubmission/set",
                new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["create"] = new JsonObject
                    {
                        ["legacy"] = new JsonObject
                        {
                            ["identityId"] = identityId,
                            ["emailId"] = JmapId.Email(emailId),
                            ["envelope"] = new JsonObject
                            {
                                ["mailFrom"] = new JsonObject
                                {
                                    ["email"] = fixture.User.Username,
                                },
                                ["rcptTo"] = new JsonArray(new JsonObject
                                {
                                    ["email"] = fixture.User.Username,
                                }),
                            },
                        },
                    },
                },
                "s1")),
        }).ConfigureAwait(false);

        Assert.IsNotNull(Arguments(submission)["notCreated"], submission.ToJsonString());
        var error = Arguments(submission)["notCreated"]!["legacy"]!;
        Assert.AreEqual("tooLarge", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(maximumMessageSize, error["maxSize"]!.GetValue<int>());
        using var verificationScope = fixture.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider
            .GetRequiredService<EmailDbContext>();
        Assert.AreEqual(0, await verificationDatabase.JmapEmailSubmissions.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await verificationDatabase.MailQueueMessages.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ImportCountsTheRawSizeOfLegacyRowsAgainstQuota scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ImportCountsTheRawSizeOfLegacyRowsAgainstQuota()
    {
        const long quota = 1_000;
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var legacyRaw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Legacy quota usage\r\n\r\n"
            + new string('x', 700));
        var importedRaw = Encoding.ASCII.GetBytes(
            "From: sender@example.net\r\n"
            + $"To: {fixture.User.Username}\r\n"
            + "Subject: Must exceed quota\r\n\r\n"
            + new string('y', 300));
        Assert.IsTrue(legacyRaw.LongLength < quota);
        Assert.IsTrue(legacyRaw.LongLength + importedRaw.LongLength > quota);

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await database.Users.SingleAsync(candidate => candidate.Id == fixture.User.Id).ConfigureAwait(false);
            user.QuotaBytes = quota;
            var inbox = await database.Folders.SingleAsync(folder =>
                folder.Id == fixture.InboxFolderId).ConfigureAwait(false);
            var emailId = Guid.CreateVersion7();
            await database.Emails.AddAsync(new EmailDB
            {
                Id = emailId,
                Sender = "sender@example.net",
                Recipient = fixture.User.Username,
                Subject = "Legacy quota usage",
                Body = Encoding.ASCII.GetString(legacyRaw),
                RawMessage = legacyRaw,
                SizeBytes = 0,
                MessageId = $"<{emailId:N}@example.net>",
                EmailObjectId = emailId.ToString("N"),
                ThreadObjectId = emailId.ToString("N"),
                ReceivedAt = DateTime.UtcNow,
                FolderId = inbox.Id,
                Uid = inbox.NextUid++,
                ModSeq = ++inbox.HighestModSeq,
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var blobId = await fixture.StoreBlobAsync(importedRaw).ConfigureAwait(false);
        var response = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Mail}}}"],
          "methodCalls": [["Email/import", {
            "accountId":"{{{fixture.AccountId}}}",
            "emails":{"overQuota":{
              "blobId":"{{{blobId}}}",
              "mailboxIds":{"{{{fixture.InboxMailboxId}}}":true}
            }}
          }, "i1"]]
        }
        """).ConfigureAwait(false);

        var error = Arguments(response)["notCreated"]!["overQuota"]!;
        Assert.AreEqual("overQuota", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
        using var verificationScope = fixture.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider
            .GetRequiredService<EmailDbContext>();
        Assert.AreEqual(1, await verificationDatabase.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The IdentityAndVacationAcceptWholeGetObjectsAsUpdates scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task IdentityAndVacationAcceptWholeGetObjectsAsUpdates()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Submission}}}", "{{{Vacation}}}"],
          "methodCalls": [
            ["Identity/get", {"accountId":"{{{fixture.AccountId}}}"}, "i1"],
            ["VacationResponse/get", {"accountId":"{{{fixture.AccountId}}}"}, "v1"]
          ]
        }
        """).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        Assert.AreEqual("Round Trip", Arguments(verify)["list"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            fixture.User.Username,
            Arguments(verify)["list"]![0]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "Whole object update",
            Arguments(verify, 1)["list"]![0]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task LazySingletonInitializationIsIdempotentAcrossConcurrentRequests()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
            Enumerable.Range(0, 8).Select(_ => fixture.InvokeAsync(request))).ConfigureAwait(false);
        foreach (var response in responses)
        {
            Assert.AreEqual("Identity/get", response["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.AreEqual("VacationResponse/get", response["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.AreEqual(1, Arguments(response)["list"]!.AsArray().Count);
            Assert.AreEqual(1, Arguments(response, 1)["list"]!.AsArray().Count);
        }

        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(1, await database.JmapIdentities.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await database.JmapVacationResponses.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task VacationSingletonCanBeConfiguredButNotCreatedOrDestroyed()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        Assert.AreEqual("singleton", Arguments(response)["notCreated"]!["other"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("singleton", Arguments(response)["notDestroyed"]!["singleton"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var vacation = Arguments(response, 1)["list"]![0]!;
        Assert.IsTrue(vacation["isEnabled"]!.GetValue<bool>());
        Assert.AreEqual("Away", vacation["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Back soon", vacation["textBody"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The VacationBodiesRoundTripThroughAzureBlobReferencesAndClearOnRemoval scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task VacationBodiesRoundTripThroughAzureBlobReferencesAndClearOnRemoval()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var firstBody = new string('x', 128 * 1024);
        var first = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Vacation}}}"],
          "methodCalls": [["VacationResponse/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"singleton":{"isEnabled":true,"textBody":"{{{firstBody}}}"}}
          }, "v1"], ["VacationResponse/get", {
            "accountId":"{{{fixture.AccountId}}}", "ids":["singleton"]
          }, "v2"]]
        }
        """).ConfigureAwait(false);
        Assert.IsNull(Arguments(first)["notUpdated"]);
        Assert.AreEqual(firstBody,
            Arguments(first, 1)["list"]![0]!["textBody"]!.GetValue<string>(), StringComparer.Ordinal);

        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var stored = await database.JmapVacationResponses.SingleAsync().ConfigureAwait(false);
            Assert.IsNull(stored.TextBody);
            Assert.IsNull(stored.HtmlBody);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, stored.BodyObjectProvider, StringComparer.Ordinal);
            Assert.IsTrue(stored.BodySizeBytes > firstBody.Length);
            Assert.IsNotNull(stored.BodyObjectName);
        }
        Assert.AreEqual(1,
            fixture.Services.GetRequiredService<InMemoryLargeObjectStore>().Count);

        var replacement = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Vacation}}}"],
          "methodCalls": [["VacationResponse/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"singleton":{"textBody":"replacement","htmlBody":"<p>away</p>"}}
          }, "v1"], ["VacationResponse/get", {
            "accountId":"{{{fixture.AccountId}}}"
          }, "v2"]]
        }
        """).ConfigureAwait(false);
        Assert.AreEqual("replacement",
            Arguments(replacement, 1)["list"]![0]!["textBody"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("<p>away</p>",
            Arguments(replacement, 1)["list"]![0]!["htmlBody"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(1,
            fixture.Services.GetRequiredService<InMemoryLargeObjectStore>().Count);

        var cleared = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}", "{{{Vacation}}}"],
          "methodCalls": [["VacationResponse/set", {
            "accountId":"{{{fixture.AccountId}}}",
            "update":{"singleton":{"textBody":null,"htmlBody":null}}
          }, "v1"], ["VacationResponse/get", {
            "accountId":"{{{fixture.AccountId}}}"
          }, "v2"]]
        }
        """).ConfigureAwait(false);
        Assert.IsNull(Arguments(cleared, 1)["list"]![0]!["textBody"]);
        Assert.IsNull(Arguments(cleared, 1)["list"]![0]!["htmlBody"]);
        Assert.AreEqual(0,
            fixture.Services.GetRequiredService<InMemoryLargeObjectStore>().Count);
    }

    [TestMethod]
    public async Task VacationResponseAllowsAnEmptyDateWindow()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
        """).ConfigureAwait(false);

        Assert.IsNull(Arguments(response)["notUpdated"]);
        Assert.IsTrue(Arguments(response)["updated"]!.AsObject().ContainsKey("singleton"));
        var vacation = Arguments(response, 1)["list"]![0]!;
        Assert.AreEqual("2026-09-30T00:00:00Z", vacation["fromDate"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("2026-09-20T00:00:00Z", vacation["toDate"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task PushMethodsRejectAccountStateAndDoNotPartiallyApplyInvalidPatch()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var subscriptionId = Guid.CreateVersion7();
        var wireId = JmapId.PushSubscription(subscriptionId);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
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
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
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
        """).ConfigureAwait(false);
        Assert.AreEqual("invalidArguments", Arguments(invalidArguments)["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidArguments", Arguments(invalidArguments, 1)["type"]!.GetValue<string>(), StringComparer.Ordinal);

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
        """).ConfigureAwait(false);
        Assert.AreEqual(
            "invalidProperties",
            Arguments(invalidPatch)["notUpdated"]![wireId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Email", Arguments(invalidPatch, 1)["list"]![0]!["types"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("forbidden", Arguments(invalidPatch, 2)["type"]!.GetValue<string>(), StringComparer.Ordinal);

        using var verificationScope = fixture.Services.CreateScope();
        var stored = await verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .JmapPushSubscriptions.AsNoTracking().SingleAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(ExpectedVector20, stored.Types!);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The PushSubscriptionAcceptsWholeGetObjectAndProtectsImmutableValues scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task PushSubscriptionAcceptsWholeGetObjectAndProtectsImmutableValues()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var subscriptionId = Guid.CreateVersion7();
        var wireId = JmapId.PushSubscription(subscriptionId);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
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
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var get = await fixture.InvokeAsync($$$"""
        {
          "using": ["{{{Core}}}"],
          "methodCalls": [["PushSubscription/get", {"ids":["{{{wireId}}}"]}, "p1"]]
        }
        """).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
        var error = Arguments(invalid)["notUpdated"]![wireId]!;
        Assert.AreEqual("invalidProperties", error["type"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            ExpectedVector21,
            error["properties"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());

        using var verificationScope = fixture.Services.CreateScope();
        var stored = await verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>()
            .JmapPushSubscriptions.AsNoTracking().SingleAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(ExpectedVector22, stored.Types!);
        Assert.IsFalse(stored.IsVerified);
        Assert.AreEqual("round-trip-device", stored.DeviceClientId, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task PushTypeFiltersTreatDuplicateNamesAsASet()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var subscriptionId = Guid.CreateVersion7();
        var wireId = JmapId.PushSubscription(subscriptionId);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
            {
                Id = subscriptionId,
                SubscriptionObjectId = wireId,
                UserId = fixture.User.Id,
                DeviceClientId = "duplicate-types-device",
                Url = "https://push.example.net/jmap",
                VerificationCode = "secret",
                Types = ["Email"],
                ExpiresAt = DateTime.UtcNow.AddDays(3),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core),
            ["methodCalls"] = new JsonArray(
                new JsonArray(
                    "PushSubscription/set",
                    new JsonObject
                    {
                        ["update"] = new JsonObject
                        {
                            [wireId] = new JsonObject
                            {
                                ["types"] = new JsonArray("Email", "Email", "Mailbox"),
                            },
                        },
                    },
                    "p1"),
                new JsonArray(
                    "PushSubscription/get",
                    new JsonObject
                    {
                        ["ids"] = new JsonArray(wireId),
                        ["properties"] = new JsonArray("id", "types"),
                    },
                    "p2")),
        }).ConfigureAwait(false);

        Assert.IsNull(Arguments(response)["notUpdated"]);
        CollectionAssert.AreEquivalent(
            ExpectedVector23,
            Arguments(response, 1)["list"]![0]!["types"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public async Task PushCreateResponseOmitsUnchangedClientProperties()
    {
        using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var receiverParameters = receiver.ExportParameters(false);
        var receiverPublic = new byte[65];
        receiverPublic[0] = 4;
        receiverParameters.Q.X!.CopyTo(receiverPublic, 1);
        receiverParameters.Q.Y!.CopyTo(receiverPublic, 33);
        var delivery = new RecordingPushPresentationClient();
        var fixture = (await JmapFixture.CreateAsync(
            configureServices: services => services.AddSingleton<IJmapPushPresentationClient>(delivery)).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var expires = JmapDate.FormatUtc(DateTime.UtcNow.AddDays(2));

        var response = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(Core),
            ["methodCalls"] = new JsonArray(new JsonArray(
                "PushSubscription/set",
                new JsonObject
                {
                    ["create"] = new JsonObject
                    {
                        ["push"] = new JsonObject
                        {
                            ["deviceClientId"] = "response-test",
                            ["url"] = "https://1.1.1.1/jmap-push",
                            ["keys"] = new JsonObject
                            {
                                ["p256dh"] = Base64Url(receiverPublic),
                                ["auth"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
                            },
                            ["verificationCode"] = null,
                            ["expires"] = expires,
                            ["types"] = new JsonArray("Email"),
                        },
                    },
                },
                "p1")),
        }).ConfigureAwait(false);

        CollectionAssert.AreEquivalent(
            ExpectedVector24,
            Arguments(response)["created"]!["push"]!.AsObject()
                .Select(property => property.Key).ToArray());
        Assert.IsTrue(delivery.VerificationPayloadLength > 0);
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

        // This makes the encrypted record exactly 4096 octets before the
        // required strictly-greater record-size adjustment.
        var plaintext = Enumerable.Repeat((byte)'x', 4079).ToArray();
        var encrypted = JmapPushEncryption.Encrypt(plaintext, publicText, authText);
        var decrypted = DecryptWebPush(encrypted, receiver, receiverPublic, auth);
        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    public async Task EncryptedPushRetainsTheJsonMediaType()
    {
        using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var receiverParameters = receiver.ExportParameters(false);
        var receiverPublic = new byte[65];
        receiverPublic[0] = 4;
        receiverParameters.Q.X!.CopyTo(receiverPublic, 1);
        receiverParameters.Q.Y!.CopyTo(receiverPublic, 33);
        using var handler = new PushRequestHandler();
        var journal = new RecordingPushJournal();
        using var delivery = new GatewayWebPushService(handler, journal, 65_536);
        var subscription = new WebPushSendRequest(
            "https://push.example.net/jmap",
            Base64Url(receiverPublic),
            Base64Url(RandomNumberGenerator.GetBytes(16)),
            DateTimeOffset.UtcNow.AddDays(1),
            Encoding.UTF8.GetBytes("{\"@type\":\"StateChange\",\"changed\":{}}"));

        var result = await delivery.SendAsync(
            subscription,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(WebPushSendOutcome.Success, result.Outcome);
        Assert.AreEqual("application/json", handler.ContentType, StringComparer.Ordinal);
        CollectionAssert.AreEqual(ExpectedVector25, handler.ContentEncodings.ToArray());
        Assert.IsTrue(handler.ContentLength > 0);
        Assert.HasCount(2, journal.Records);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, journal.Records[0].Direction, StringComparer.Ordinal);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, journal.Records[1].Direction, StringComparer.Ordinal);
    }

    [TestMethod]
    public void SearchSnippetNeverSplitsUnicodeScalars()
    {
        var value = new string('x', 10)
            + "😀"
            + new string('y', 79)
            + "needle"
            + new string('z', 220);
        var window = MailSearchSnippetText.SelectPreview(value, ["needle"]);
        var snippet = GatewaySearchSnippetFormatter.HighlightPreviewWindow(window, ["needle"]);

        Assert.IsNotNull(snippet);
        Assert.IsTrue(JmapJson.ContainsOnlyUnicodeScalars(snippet));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(snippet) <= 255);
        StringAssert.Contains(snippet, "<mark>needle</mark>", StringComparison.Ordinal);
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
        """).ConfigureAwait(false);
    }

    private static byte[] BuildDeepMultipartMessage(
        string recipient,
        int depth,
        string payload)
    {
        var raw = new StringBuilder(
            $"From: sender@example.net\r\nTo: {recipient}\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=b0\r\n\r\n");
        for (var index = 0; index < depth; index++)
        {
            raw.Append("--b").Append(index).Append("\r\n");
            if (index == depth - 1)
            {
                raw.Append("Content-Type: text/plain; charset=us-ascii\r\n\r\n")
                    .Append(payload)
                    .Append("\r\n");
            }
            else
            {
                raw.Append("Content-Type: multipart/mixed; boundary=b")
                    .Append(index + 1)
                    .Append("\r\n\r\n");
            }
        }
        for (var index = depth - 1; index >= 0; index--)
            raw.Append("--b").Append(index).Append("--\r\n");
        return Encoding.ASCII.GetBytes(raw.ToString());
    }

    private static async Task<string> GetStateAsync(JmapFixture fixture, string dataType)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JmapStateService>()
            .GetStateAsync(fixture.InboxId, dataType).ConfigureAwait(false);
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
        Assert.IsTrue(recordSize > content.Length - 21 - senderLength);
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

    private sealed class PushRequestHandler : HttpMessageHandler
    {
        public string? ContentType { get; private set; }
        public IReadOnlyList<string> ContentEncodings { get; private set; } = [];
        public int ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            ContentEncodings = request.Content?.Headers.ContentEncoding.ToArray() ?? [];
            ContentLength = (await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)).Length;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private sealed class RecordingPushPresentationClient : IJmapPushPresentationClient
    {
        public ApplicationRequest? CreateVerificationRequest(string url, string? keysJson, DateTime expiresAt, JmapPushMessage payload) =>
            throw new AssertFailedException("The InMemory fixture uses its post-commit test callback.");

        public int VerificationPayloadLength { get; private set; }

        public Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<WebPushSendOutcome> SendAsync(
            string url,
            string? keysJson,
            DateTime expiresAt,
            JmapPushMessage payload,
            CancellationToken cancellationToken) =>
            Task.FromResult(WebPushSendOutcome.Success);

        public Task EnqueueVerificationAsync(
            string url,
            string? keysJson,
            DateTime expiresAt,
            JmapPushMessage payload,
            CancellationToken cancellationToken)
        {
            VerificationPayloadLength = GatewayJmapChangesCodec.EncodePush(payload).Length;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPushJournal : IGatewayTrafficJournal
    {
        public List<GatewayTrafficRecord> Records { get; } = [];

        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GatewayTrafficRecord>>(
                Records.Where(record => record.SessionId == sessionId).ToArray());
    }
    private static readonly string[] ExpectedVector1 = new[] { "parentId", "role", "sortOrder", "isSubscribed" };
    private static readonly string[] ExpectedVector2 = new[] { "valid@example.test", "\"quoted local\"@example.test" };
    private static readonly string[] ExpectedVector3 = new[] { "https://example.test/help", "mailto:help@example.test" };
    private static readonly string[] ExpectedVector4 = new[] { "https://example.test/subscribe" };
    private static readonly string[] ExpectedVector5 = new[] { "https://example.test/first" };
    private static readonly string[] ExpectedVector6 = new[] { " \t=?UTF-8?Q?already_encoded?=\r\n\tcontinued", "second" };
    private static readonly int[] ExpectedVector7 = new[] { 0, 1 };
    private static readonly string[] ExpectedVector8 = new[] { "first@example.test", "second@example.test" };
    private static readonly string[] ExpectedVector9 = new[] { "messageId", "sender" };
    private static readonly string[] ExpectedVector10 = new[] { "part@example.test" };
    private static readonly string[] ExpectedVector11 = new[] { "needle" };
    private static readonly string[] ExpectedVector12 = new[]
                {
                "id", "threadId", "sendAt", "undoStatus", "deliveryStatus",
                "dsnBlobIds", "mdnBlobIds",
            };
    private static readonly string[] ExpectedVector13 = new[]
                {
                "id", "threadId", "envelope", "sendAt", "undoStatus", "deliveryStatus",
                "dsnBlobIds", "mdnBlobIds",
            };
    private static readonly string[] ExpectedVector14 = new[] { "id", "mayDelete" };
    private static readonly string[] ExpectedVector15 = new[] { "id", "name", "replyTo", "bcc", "textSignature", "htmlSignature", "mayDelete" };
    private static readonly string[] ExpectedVector16 = new[] { "from", "sentAt", "to" };
    private static readonly string[] ExpectedVector17 = new[] { "inReplyTo", "messageId", "references" };
    private static readonly string[] ExpectedVector18 = new[] { "header:Resent-Date:asDate:all", "sentAt" };
    private static readonly string[] ExpectedVector19 = new[]
                {
                "cc",
                "header:Resent-Cc:asAddresses:all",
                "header:Resent-Date:asDate:all",
                "header:Resent-Message-ID:asMessageIds:all",
                "header:Resent-Reply-To:asAddresses:all",
                "header:Resent-Sender:asAddresses:all",
                "header:Resent-To:asAddresses:all",
                "headers",
                "replyTo",
                "to",
            };
    private static readonly string[] ExpectedVector20 = new[] { "Email" };
    private static readonly string[] ExpectedVector21 = new[] { "deviceClientId" };
    private static readonly string[] ExpectedVector22 = new[] { "Mailbox", "Email" };
    private static readonly string[] ExpectedVector23 = new[] { "Email", "Mailbox" };
    private static readonly string[] ExpectedVector24 = new[] { "id" };
    private static readonly string[] ExpectedVector25 = new[] { "aes128gcm" };
}
