using System.Text.Json.Nodes;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapCoreTests
{
    [TestMethod]
    public async Task SessionPublishesCoreEndpointsAndAccessibleAccount()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var storedInbox = await database.Inboxes
            .Include(inbox => inbox.Owner)
            .Include(inbox => inbox.Address)
            .ThenInclude(address => address.Company)
            .SingleAsync(inbox => inbox.Id == fixture.InboxId);
        Assert.AreEqual(fixture.User.Id, storedInbox.OwnerId);
        Assert.IsTrue(storedInbox.Owner.IsActive);
        Assert.IsTrue(storedInbox.Address.IsActive);
        Assert.IsTrue(storedInbox.Address.Company.IsActive);
        var session = await scope.ServiceProvider
            .GetRequiredService<JmapSessionService>()
            .BuildAsync(fixture.User);

        Assert.AreEqual("https://email.mk8n.com/jmap/api", session.Value["apiUrl"]?.GetValue<string>());
        Assert.AreEqual(
            "https://email.mk8n.com/jmap/upload/{accountId}",
            session.Value["uploadUrl"]?.GetValue<string>());
        Assert.IsTrue(session.State.StartsWith('S'));

        var capabilities = session.Value["capabilities"]?.AsObject();
        Assert.IsNotNull(capabilities);
        Assert.IsTrue(capabilities.ContainsKey(JmapConstants.CoreCapability));
        var collations = capabilities[JmapConstants.CoreCapability]!["collationAlgorithms"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        CollectionAssert.AreEqual(
            JmapCollation.SupportedIdentifiers.ToArray(),
            collations);
        var accounts = session.Value["accounts"]?.AsObject();
        Assert.IsNotNull(accounts);
        var accountId = JmapId.Account(fixture.InboxId);
        Assert.IsTrue(accounts.ContainsKey(accountId), accounts.ToJsonString());
        Assert.AreEqual(
            fixture.User.Username,
            accounts[accountId]?["name"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task CoreEchoSupportsBatchesAndResultReferences()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var request = JsonNode.Parse(
            """
            {
              "using": ["urn:ietf:params:jmap:core"],
              "methodCalls": [
                ["Core/echo", {"values":[{"id":"a"},{"id":"b"}]}, "c1"],
                ["Core/echo", {
                  "#ids": {
                    "resultOf": "c1",
                    "name": "Core/echo",
                    "path": "/values/*/id"
                  }
                }, "c2"]
              ]
            }
            """);

        var response = await processor.ProcessAsync(request, fixture.User);
        var methodResponses = response["methodResponses"]?.AsArray();
        Assert.IsNotNull(methodResponses);
        Assert.AreEqual(2, methodResponses.Count);
        var referencedIds = methodResponses[1]?[1]?["ids"]?.AsArray();
        Assert.IsNotNull(referencedIds);
        CollectionAssert.AreEqual(
            new[] { "a", "b" },
            referencedIds.Select(value => value!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task CoreRejectsUnknownCapabilitiesAndInvalidReferences()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();

        var unknownCapability = JsonNode.Parse(
            """{"using":["urn:example:unknown"],"methodCalls":[]}""");
        var exception = await Assert.ThrowsAsync<JmapRequestException>(
            () => processor.ProcessAsync(unknownCapability, fixture.User));
        Assert.AreEqual("urn:ietf:params:jmap:error:unknownCapability", exception.Type);

        var invalidReference = JsonNode.Parse(
            """
            {
              "using": ["urn:ietf:params:jmap:core"],
              "methodCalls": [["Core/echo", {
                "#ids": {"resultOf":"missing","name":"Core/echo","path":"/ids"}
              }, "c1"]]
            }
            """);
        var response = await processor.ProcessAsync(invalidReference, fixture.User);
        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[0]?[1]?["type"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task CoreIgnoresUnknownRequestPropertiesAndEchoesSuppliedCreatedIdsOnly()
    {
        await using var fixture = await JmapFixture.CreateAsync();

        var withCreatedIds = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[],
              "createdIds":{},
              "futureExtension":{"enabled":true}
            }
            """);
        Assert.IsInstanceOfType<JsonObject>(withCreatedIds["createdIds"]);
        Assert.AreEqual(0, withCreatedIds["createdIds"]!.AsObject().Count);

        var withoutCreatedIds = await fixture.InvokeAsync(
            """{"using":["urn:ietf:params:jmap:core"],"methodCalls":[]}""");
        Assert.IsFalse(withoutCreatedIds.ContainsKey("createdIds"));

        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var nullCreatedIds = JsonNode.Parse(
            """{"using":["urn:ietf:params:jmap:core"],"methodCalls":[],"createdIds":null}""");
        var exception = await Assert.ThrowsAsync<JmapRequestException>(
            () => processor.ProcessAsync(nullCreatedIds, fixture.User));
        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", exception.Type);
    }

    [TestMethod]
    public async Task ResultReferencesOnlyMapArraysAndUseStrictJsonPointerIndexes()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[
                ["Core/echo",{"object":{"one":{"id":"a"}}},"c1"],
                ["Core/echo",{"#value":{"resultOf":"c1","name":"Core/echo","path":"/object/*/id"}},"c2"],
                ["Core/echo",{"array":["zero","one"]},"c3"],
                ["Core/echo",{"#value":{"resultOf":"c3","name":"Core/echo","path":"/array/01"}},"c4"]
              ]
            }
            """);

        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[1]?[1]?["type"]?.GetValue<string>());
        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[3]?[1]?["type"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task ResultReferencesOnlySelectTheFirstResponseForACallId()
    {
        await using var fixture = await JmapFixture.CreateAsync(
            configureServices: services => services.AddSingleton<IJmapMethod>(
                new ImplicitResponseMethod()));
        var response = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[
                ["Test/implicit",{},"c1"],
                ["Core/echo",{
                  "#value":{"resultOf":"c1","name":"Test/additional","path":"/value"}
                },"c2"]
              ]
            }
            """);

        Assert.AreEqual(
            "Test/implicit",
            response["methodResponses"]?[0]?[0]?.GetValue<string>());
        Assert.AreEqual(
            "Test/additional",
            response["methodResponses"]?[1]?[0]?.GetValue<string>());
        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[2]?[1]?["type"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task JsonTransportEnforcesExactMediaTypeAndNamedLimits()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json; charset=utf-8";
        Assert.IsTrue(JmapEndpointRouteBuilderExtensions.HasJmapJsonContentType(context.Request));
        context.Request.ContentType = "application/problem+json";
        Assert.IsFalse(JmapEndpointRouteBuilderExtensions.HasJmapJsonContentType(context.Request));

        var configuration = new JmapConfig { MaxRequestSizeBytes = 65_536 };
        context.Request.ContentLength = 65_537;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var exception = await Assert.ThrowsAsync<JmapRequestException>(
            () => JmapJson.ParseRequestAsync(context.Request, configuration, CancellationToken.None));
        Assert.AreEqual("urn:ietf:params:jmap:error:limit", exception.Type);
        Assert.AreEqual("maxSizeRequest", exception.Limit);
        Assert.AreEqual(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    [TestMethod]
    public void BinaryEndpointsNormalizeOnlyRfc6838MediaTypes()
    {
        Assert.AreEqual(
            "text/plain",
            JmapEndpointRouteBuilderExtensions.NormalizeMediaType(
                " Text/Plain ; charset=utf-8 "));
        Assert.AreEqual(
            "application/vnd.example.mail+json",
            JmapEndpointRouteBuilderExtensions.NormalizeMediaType(
                "application/vnd.example.mail+json"));

        foreach (var invalid in new string?[]
        {
            null,
            "",
            "*/*",
            "text/pl%ain",
            "text/plain, application/json",
            "text/plain; charset=\"unterminated",
            $"application/{new string('a', 128)}",
        })
        {
            Assert.AreEqual(
                "application/octet-stream",
                JmapEndpointRouteBuilderExtensions.NormalizeMediaType(invalid),
                invalid);
        }

        Assert.AreEqual(
            $"{new string('a', 127)}/{new string('b', 127)}",
            JmapEndpointRouteBuilderExtensions.NormalizeMediaType(
                $"{new string('A', 127)}/{new string('B', 127)}"));
    }

    [TestMethod]
    public void BinaryDownloadPreservesTheSuppliedFilename()
    {
        var name = $"reports/{new string('é', 260)}.txt";
        var result = JmapEndpointRouteBuilderExtensions.CreateDownloadResult(
            [1, 2, 3],
            "text/plain",
            name);

        var file = Assert.IsInstanceOfType<FileContentHttpResult>(result);
        Assert.AreEqual(name, file.FileDownloadName);
        Assert.AreEqual("text/plain", file.ContentType);
        Assert.IsTrue(file.EnableRangeProcessing);
    }

    [TestMethod]
    public async Task JsonTransportRejectsUnpairedUnicodeSurrogates()
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"\\uD800\"}");
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<JmapRequestException>(
            () => JmapJson.ParseRequestAsync(
                context.Request,
                new JmapConfig { MaxRequestSizeBytes = 65_536 },
                CancellationToken.None));

        Assert.AreEqual("urn:ietf:params:jmap:error:notJSON", exception.Type);
    }

    [TestMethod]
    public async Task ConcurrentRequestLimitRejectsInsteadOfQueuing()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var limiter = fixture.Services.GetRequiredService<JmapConcurrencyLimiter>();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < fixture.Configuration.Jmap.MaxConcurrentRequests; index++)
                leases.Add(await limiter.AcquireRequestAsync(CancellationToken.None));

            var exception = await Assert.ThrowsAsync<JmapRequestException>(
                () => limiter.AcquireRequestAsync(CancellationToken.None));
            Assert.AreEqual("maxConcurrentRequests", exception.Limit);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void JmapDatesEnforceNormalizedRfc3339AndPreserveFractions()
    {
        Assert.IsTrue(JmapDate.TryParseUtcDate("2026-09-20T12:34:56.123456789Z", out var utc));
        Assert.AreEqual("2026-09-20T12:34:56.1234567Z", JmapDate.FormatUtc(utc));
        Assert.IsTrue(JmapDate.TryParseDate("2026-09-20T14:34:56.25+02:00", out var offset));
        Assert.AreEqual("2026-09-20T14:34:56.25+02:00", JmapDate.FormatDate(offset));

        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20T12:34:56.000Z", out _));
        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20t12:34:56z", out _));
        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20T14:34:56+02:00", out _));
        Assert.IsFalse(JmapDate.TryParseDate("2026-02-30T12:34:56Z", out _));
    }

    [TestMethod]
    public void JmapIntegersAndAdvertisedCollationsFollowCoreRanges()
    {
        var values = JsonNode.Parse(
            """
            {
              "minimum": -9007199254740991,
              "maximum": 9007199254740991,
              "tooSmall": -9007199254740992,
              "tooLarge": 9007199254740992,
              "unsigned": 4294967296
            }
            """)!.AsObject();

        Assert.IsTrue(JmapMethodHelpers.TryGetOptionalInt(
            values, "minimum", 0, out var minimum));
        Assert.AreEqual(JmapMethodHelpers.MinimumInt, minimum);
        Assert.IsTrue(JmapMethodHelpers.TryGetOptionalInt(
            values, "maximum", 0, out var maximum));
        Assert.AreEqual(JmapMethodHelpers.MaximumInt, maximum);
        Assert.IsFalse(JmapMethodHelpers.TryGetOptionalInt(
            values, "tooSmall", 0, out _));
        Assert.IsFalse(JmapMethodHelpers.TryGetOptionalUnsignedInt(
            values, "tooLarge", out _));
        Assert.IsTrue(JmapMethodHelpers.TryGetOptionalUnsignedInt(
            values, "unsigned", out var unsigned));
        Assert.AreEqual(4_294_967_296L, unsigned);

        Assert.AreEqual(0, JmapCollation.Compare(
            "04294967298tail", "4294967298", "i;ascii-numeric"));
        Assert.AreEqual(0, JmapCollation.Compare("", "not-a-number", "i;ascii-numeric"));
        Assert.IsTrue(JmapCollation.Compare("1", "", "i;ascii-numeric") < 0);
        Assert.AreEqual(0, JmapCollation.Compare("Inbox", "iNBOX", "i;ascii-casemap"));
    }

    [TestMethod]
    [DataRow("\t Re [list] :   Topic  (fwd)\t", "Topic")]
    [DataRow("[mailing-list] Re: Topic", "Topic")]
    [DataRow("Re: [mailing-list] Topic", "Topic")]
    [DataRow("[fwd: Re: [mailing-list] Topic (fwd)]", "Topic")]
    [DataRow("[fwd: [fwd: Topic]]", "Topic")]
    [DataRow("[one] [two]", "[two]")]
    [DataRow("[only]", "[only]")]
    [DataRow("re[2]:topic", "topic")]
    [DataRow("Regarding: Topic", "Regarding: Topic")]
    public void EmailSubjectSortUsesTheRfc5256BaseSubject(string subject, string expected)
    {
        Assert.AreEqual(expected, JmapEmailQueryEngine.BaseSubject(subject));
    }

    [TestMethod]
    public void OptionalNonNullableArgumentsRejectExplicitNull()
    {
        var values = JsonNode.Parse(
            """{"flag":null,"position":null,"size":null,"name":null,"items":null}""")!
            .AsObject();

        Assert.IsFalse(JmapMethodHelpers.TryGetOptionalBoolean(values, "flag", false, out _));
        Assert.IsFalse(JmapMethodHelpers.TryGetOptionalInt(values, "position", 0, out _));
        Assert.IsFalse(JmapMethodHelpers.TryGetOptionalUnsignedInt(
            values,
            "size",
            out _,
            allowNull: false));
        Assert.IsFalse(JmapMethodHelpers.TryGetOptionalString(
            values,
            "name",
            out _,
            allowNull: false));
        Assert.IsFalse(JmapMethodHelpers.TryGetStringArray(values, "items", false, out _));

        Assert.IsTrue(JmapMethodHelpers.TryGetOptionalUnsignedInt(values, "size", out _));
        Assert.IsTrue(JmapMethodHelpers.TryGetOptionalString(values, "name", out _));
        Assert.IsTrue(JmapMethodHelpers.TryGetStringArray(values, "items", true, out _));
    }

    [TestMethod]
    public void StringArrayArgumentsPermitRepeatedProjectionProperties()
    {
        var values = JsonNode.Parse("""{"properties":["id","id","name"]}""")!
            .AsObject();

        Assert.IsTrue(JmapMethodHelpers.TryGetStringArray(
            values,
            "properties",
            true,
            out var properties));
        CollectionAssert.AreEqual(
            new[] { "id", "id", "name" },
            properties!.ToArray());
    }

    [TestMethod]
    public async Task ChangesLimitCountsFoldedObjectIdsRatherThanLogRows()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        database.JmapChanges.AddRange(
            new JmapChangeDB
            {
                AccountId = fixture.InboxId,
                DataType = "Test",
                ObjectId = "one",
                ChangeKind = JmapConstants.CreatedChange,
                ChangedAt = DateTime.UtcNow,
            },
            new JmapChangeDB
            {
                AccountId = fixture.InboxId,
                DataType = "Test",
                ObjectId = "one",
                ChangeKind = JmapConstants.UpdatedChange,
                ChangedAt = DateTime.UtcNow,
            },
            new JmapChangeDB
            {
                AccountId = fixture.InboxId,
                DataType = "Test",
                ObjectId = "two",
                ChangeKind = JmapConstants.CreatedChange,
                ChangedAt = DateTime.UtcNow,
            });
        await database.SaveChangesAsync();

        var states = scope.ServiceProvider.GetRequiredService<JmapStateService>();
        var first = await states.GetChangesAsync(
            fixture.InboxId,
            "Test",
            "s0",
            1,
            100,
            CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.IsTrue(first.HasMoreChanges);
        CollectionAssert.AreEqual(new[] { "one" }, first.Created.ToArray());
        Assert.AreEqual(1, first.Created.Count + first.Updated.Count + first.Destroyed.Count);

        var second = await states.GetChangesAsync(
            fixture.InboxId,
            "Test",
            first.NewState,
            1,
            100,
            CancellationToken.None);
        Assert.IsNotNull(second);
        Assert.IsFalse(second.HasMoreChanges);
        CollectionAssert.AreEqual(new[] { "two" }, second.Created.ToArray());
    }

    [TestMethod]
    public async Task TypedIdArgumentsRejectValuesOutsideTheIdAlphabet()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core","urn:ietf:params:jmap:mail"],
              "methodCalls":[
                ["Email/get",{"accountId":"ACCOUNT","ids":["not an id"]},"c1"],
                ["Mailbox/query",{"accountId":"ACCOUNT","anchor":"not/an/id"},"c2"]
              ]
            }
            """.Replace("ACCOUNT", JmapId.Account(fixture.InboxId), StringComparison.Ordinal));

        Assert.AreEqual(
            "invalidArguments",
            response["methodResponses"]?[0]?[1]?["type"]?.GetValue<string>());
        Assert.AreEqual(
            "invalidArguments",
            response["methodResponses"]?[1]?[1]?["type"]?.GetValue<string>());
    }

    private sealed class ImplicitResponseMethod : IJmapMethod
    {
        public string Name => "Test/implicit";
        public string Capability => JmapConstants.CoreCapability;

        public Task<JmapMethodResponse> InvokeAsync(
            JmapInvocationContext context,
            JsonObject arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new JmapMethodResponse(
                Name,
                new JsonObject { ["value"] = "primary" },
                [new JmapMethodResponse(
                    "Test/additional",
                    new JsonObject { ["value"] = "additional" })]));
    }
}
