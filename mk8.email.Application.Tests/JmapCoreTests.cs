using System.Text.Json.Nodes;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapCoreTests
{
    [TestMethod]
    public async Task SessionPublishesCoreEndpointsAndAccessibleAccount()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var storedInbox = await database.Inboxes
            .Include(inbox => inbox.Owner)
            .Include(inbox => inbox.Address)
            .ThenInclude(address => address.Company)
            .SingleAsync(inbox => inbox.Id == fixture.InboxId).ConfigureAwait(false);
        Assert.AreEqual(fixture.User.Id, storedInbox.OwnerId);
        Assert.IsTrue(storedInbox.Owner.IsActive);
        Assert.IsTrue(storedInbox.Address.IsActive);
        Assert.IsTrue(storedInbox.Address.Company.IsActive);
        var profile = await scope.ServiceProvider
            .GetRequiredService<JmapAccountProfileService>()
            .GetProfileAsync(fixture.User).ConfigureAwait(false);
        var session = GatewayJmapProfileCodec.Render(profile, fixture.Configuration);

        Assert.AreEqual("https://email.mk8n.com/jmap/api", session["apiUrl"]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "https://email.mk8n.com/jmap/upload/{accountId}",
            session["uploadUrl"]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsTrue(session["state"]!.GetValue<string>().StartsWith('S'));

        var capabilities = session["capabilities"]?.AsObject();
        Assert.IsNotNull(capabilities);
        Assert.IsTrue(capabilities.ContainsKey(GatewayJmapFeatureCodec.CoreCapability));
        var collations = capabilities[GatewayJmapFeatureCodec.CoreCapability]!["collationAlgorithms"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        CollectionAssert.AreEqual(
            JmapCollation.SupportedIdentifiers.ToArray(),
            collations);
        var accounts = session["accounts"]?.AsObject();
        Assert.IsNotNull(accounts);
        var accountId = JmapId.Account(fixture.InboxId);
        Assert.IsTrue(accounts.ContainsKey(accountId), accounts.ToJsonString());
        Assert.AreEqual(
            fixture.User.Username,
            accounts[accountId]?["name"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task CoreEchoSupportsBatchesAndResultReferences()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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

        var response = await JmapFixture.ProcessAsync(processor, request, fixture.User).ConfigureAwait(false);
        var methodResponses = response["methodResponses"]?.AsArray();
        Assert.IsNotNull(methodResponses);
        Assert.AreEqual(2, methodResponses.Count);
        var referencedIds = methodResponses[1]?[1]?["ids"]?.AsArray();
        Assert.IsNotNull(referencedIds);
        CollectionAssert.AreEqual(
            ExpectedVector1,
            referencedIds.Select(value => value!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task MalformedInvocationRejectsRequestBeforeEarlierCallsRun()
    {
        var invocationCount = 0;
        var fixture = (await JmapFixture.CreateAsync(
            configureServices: services => JmapFixture.OverrideMethod(services,
                new InvocationProbeMethod(() => invocationCount++))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);

        var exception = await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() => fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[
                ["Mailbox/query",{},"c1"],
                ["Core/echo",{}]
              ]
            }
            """)).ConfigureAwait(false);

        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", exception.Problem.Type, StringComparer.Ordinal);
        Assert.AreEqual(0, invocationCount);
    }

    [TestMethod]
    public async Task FailedMethodsRestoreCreationIdsAndDiscardPostCommitActions()
    {
        var failedPostCommitCount = 0;
        var successfulPostCommitCount = 0;
        var fixture = (await JmapFixture.CreateAsync(
            configureServices: services =>
            {
                JmapFixture.OverrideMethod(services, new AtomicityProbeMethod(
                    MailOperationKind.FindFolders,
                    "transient",
                    "E11111111111111111111111111111111",
                    () => failedPostCommitCount++,
                    true));
                JmapFixture.OverrideMethod(services, new AtomicityProbeMethod(
                    MailOperationKind.MutateFolders,
                    "kept",
                    "E22222222222222222222222222222222",
                    () => successfulPostCommitCount++,
                    false));
            }).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);

        var response = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "createdIds":{"existing":"E00000000000000000000000000000000"},
              "methodCalls":[
                ["Mailbox/query",{},"f1"],
                ["Mailbox/set",{},"s1"]
              ]
            }
            """).ConfigureAwait(false);

        Assert.AreEqual("serverFail", response["methodResponses"]![0]![1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Mailbox/set", response["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        var createdIds = response["createdIds"]!.AsObject();
        Assert.IsTrue(createdIds.ContainsKey("existing"));
        Assert.IsFalse(createdIds.ContainsKey("transient"));
        Assert.AreEqual(
            "E22222222222222222222222222222222",
            createdIds["kept"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, failedPostCommitCount);
        Assert.AreEqual(1, successfulPostCommitCount);
    }

    [TestMethod]
    public async Task CommittedPostCommitActionsIgnoreRequestCancellation()
    {
        var completed = false;
        using var cancellation = new CancellationTokenSource();
        var fixture = (await JmapFixture.CreateAsync(
            configureServices: services => JmapFixture.OverrideMethod(services,
                new PostCommitCancellationProbeMethod(
                    cancellation,
                    () => completed = true))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var request = JsonNode.Parse(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[["Mailbox/query",{},"c1"]]
            }
            """);

        try
        {
            _ = await JmapFixture.ProcessAsync(processor, request, fixture.User, cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(completed);
    }

    [TestMethod]
    public async Task CoreRejectsUnknownCapabilitiesAndInvalidReferences()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();

        var unknownCapability = JsonNode.Parse(
            """{"using":["urn:example:unknown"],"methodCalls":[]}""");
        var exception = await Assert.ThrowsAsync<MailApplicationException>(
            () => JmapFixture.ProcessAsync(processor, unknownCapability, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.UnsupportedFeature, exception.Failure.Kind);

        var invalidReference = JsonNode.Parse(
            """
            {
              "using": ["urn:ietf:params:jmap:core"],
              "methodCalls": [["Core/echo", {
                "#ids": {"resultOf":"missing","name":"Core/echo","path":"/ids"}
              }, "c1"]]
            }
            """);
        var response = await JmapFixture.ProcessAsync(processor, invalidReference, fixture.User).ConfigureAwait(false);
        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[0]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task CoreTreatsUsingAsASetAndReportsUnknownStringCapabilities()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);

        var duplicate = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core","urn:ietf:params:jmap:core"],
              "methodCalls":[["Core/echo",{"ok":true},"c1"]]
            }
            """).ConfigureAwait(false);
        Assert.AreEqual(
            true,
            duplicate["methodResponses"]?[0]?[1]?["ok"]?.GetValue<bool>());

        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var emptyCapability = JsonNode.Parse(
            """{"using":["urn:ietf:params:jmap:core",""],"methodCalls":[]}""");
        var exception = await Assert.ThrowsAsync<MailApplicationException>(
            () => JmapFixture.ProcessAsync(processor, emptyCapability, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.UnsupportedFeature, exception.Failure.Kind);
    }

    [TestMethod]
    public async Task CoreIgnoresUnknownRequestPropertiesAndEchoesSuppliedCreatedIdsOnly()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);

        var withCreatedIds = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[],
              "createdIds":{},
              "futureExtension":{"enabled":true}
            }
            """).ConfigureAwait(false);
        Assert.IsInstanceOfType<JsonObject>(withCreatedIds["createdIds"]);
        Assert.AreEqual(0, withCreatedIds["createdIds"]!.AsObject().Count);

        var withoutCreatedIds = await fixture.InvokeAsync(
            """{"using":["urn:ietf:params:jmap:core"],"methodCalls":[]}""").ConfigureAwait(false);
        Assert.IsFalse(withoutCreatedIds.ContainsKey("createdIds"));

        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var nullCreatedIds = JsonNode.Parse(
            """{"using":["urn:ietf:params:jmap:core"],"methodCalls":[],"createdIds":null}""");
        var exception = await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(
            () => JmapFixture.ProcessAsync(processor, nullCreatedIds, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", exception.Problem.Type, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ResultReferencesOnlyMapArraysAndUseStrictJsonPointerIndexes()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
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
            """).ConfigureAwait(false);

        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[1]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[3]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ResultReferencesOnlySelectTheFirstResponseForACallId()
    {
        var fixture = (await JmapFixture.CreateAsync(
            configureServices: services => JmapFixture.OverrideMethod(services,
                new ImplicitResponseMethod())).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[
                ["Mailbox/query",{},"c1"],
                ["Core/echo",{
                  "#value":{"resultOf":"c1","name":"Email/get","path":"/value"}
                },"c2"]
              ]
            }
            """).ConfigureAwait(false);

        Assert.AreEqual(
            "Mailbox/query",
            response["methodResponses"]?[0]?[0]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "Email/get",
            response["methodResponses"]?[1]?[0]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidResultReference",
            response["methodResponses"]?[2]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void JsonTransportEnforcesExactMediaTypeAndNamedLimits()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json; charset=utf-8";
        Assert.IsTrue(JmapEndpointRouteBuilderExtensions.HasJmapJsonContentType(context.Request));
        context.Request.ContentType = "application/problem+json";
        Assert.IsFalse(JmapEndpointRouteBuilderExtensions.HasJmapJsonContentType(context.Request));

        var configuration = new JmapConfig { MaxRequestSizeBytes = 65_536 };
        var exception = Assert.ThrowsExactly<GatewayJmapBatchCodec.RequestException>(
            () => GatewayJmapJson.ParseRequest(new byte[65_537], configuration));
        Assert.AreEqual("urn:ietf:params:jmap:error:limit", exception.Problem.Type, StringComparer.Ordinal);
        Assert.AreEqual("maxSizeRequest", exception.Problem.Limit, StringComparer.Ordinal);
    }

    [TestMethod]
    public void BinaryEndpointsNormalizeOnlyRfc6838MediaTypes()
    {
        Assert.AreEqual(
            "text/plain",
            JmapEndpointRouteBuilderExtensions.NormalizeMediaType(
                " Text/Plain ; charset=utf-8 "), StringComparer.Ordinal);
        Assert.AreEqual(
            "application/vnd.example.mail+json",
            JmapEndpointRouteBuilderExtensions.NormalizeMediaType(
                "application/vnd.example.mail+json"), StringComparer.Ordinal);

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
StringComparer.Ordinal, invalid);
        }

        Assert.AreEqual(
            $"{new string('a', 127)}/{new string('b', 127)}",
            JmapEndpointRouteBuilderExtensions.NormalizeMediaType(
                $"{new string('A', 127)}/{new string('B', 127)}"), StringComparer.Ordinal);
    }

    [TestMethod]
    public void EventSourcePingAcceptsUnsignedIntervalsAndClampsSafely()
    {
        foreach (var (value, expected) in new (string Value, int Expected)[]
        {
            ("0", 0),
            ("000", 0),
            ("1", 15),
            ("30", 30),
            ("300", 300),
            ("2147483648", 300),
            (new string('9', 100), 300),
        })
        {
            Assert.IsTrue(
                JmapEndpointRouteBuilderExtensions.TryNormalizeEventSourcePing(value, out var actual),
                value);
            Assert.AreEqual(expected, actual, value);
        }

        foreach (var invalid in new[] { "", "-1", "+1", " 1", "1 ", "1.0", "1e2", "١" })
        {
            Assert.IsFalse(
                JmapEndpointRouteBuilderExtensions.TryNormalizeEventSourcePing(invalid, out _),
                invalid);
        }
    }

    [TestMethod]
    public void EventSourceTypeFiltersTreatDuplicateNamesAsASet()
    {
        Assert.IsTrue(JmapEndpointRouteBuilderExtensions.TryEventTypes(
            "Email,Email,Mailbox",
            out var types));
        CollectionAssert.AreEquivalent(
            ExpectedVector2,
            types!.ToArray());
    }

    [TestMethod]
    public void EmailBodyMediaTypesUseRfc6838RestrictedNames()
    {
        Assert.IsTrue(JmapMediaType.TryNormalize(
            "Application/Vnd.Example+Json",
            out var normalized));
        Assert.AreEqual("application/vnd.example+json", normalized, StringComparer.Ordinal);

        foreach (var invalid in new[]
        {
            "+application/json",
            "application/+json",
            "application/json; charset=utf-8",
            "application/json/extra",
            $"application/{new string('a', 128)}",
        })
        {
            Assert.IsFalse(JmapMediaType.TryNormalize(invalid, out _), invalid);
        }
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
        Assert.AreEqual(name, file.FileDownloadName, StringComparer.Ordinal);
        Assert.AreEqual("text/plain", file.ContentType, StringComparer.Ordinal);
        Assert.IsTrue(file.EnableRangeProcessing);
    }

    [TestMethod]
    public async Task BlobCopyRejectsTheSameSourceAndDestinationAccount()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(
            $$"""
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[["Blob/copy",{
                "fromAccountId":"{{fixture.AccountId}}",
                "accountId":"{{fixture.AccountId}}",
                "blobIds":[]
              },"c1"]]
            }
            """).ConfigureAwait(false);

        Assert.AreEqual(
            "error",
            response["methodResponses"]?[0]?[0]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidArguments",
            response["methodResponses"]?[0]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void JsonTransportRejectsSurrogatesAndUnicodeNoncharacters()
    {
        foreach (var json in new[]
        {
            "{\"value\":\"\\uD800\"}",
            "{\"value\":\"\\uFDD0\"}",
            "{\"value\":\"\\uFFFF\"}",
            "{\"value\":\"\\uD83F\\uDFFE\"}",
            "{\"\\uFDEF\":true}",
        })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var exception = Assert.ThrowsExactly<GatewayJmapBatchCodec.RequestException>(
                () => GatewayJmapJson.ParseRequest(
                    bytes,
                    new JmapConfig { MaxRequestSizeBytes = 65_536 }));

            Assert.AreEqual("urn:ietf:params:jmap:error:notJSON", exception.Problem.Type, StringComparer.Ordinal, json);
        }
    }

    [TestMethod]
    public async Task ResponsesReplaceCharactersForbiddenByIJsonBeforeResultReferences()
    {
        var fixture = (await JmapFixture.CreateAsync(
            configureServices: services => JmapFixture.OverrideMethod(services,
                new InvalidUnicodeResponseMethod())).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(
            """
            {
              "using":["urn:ietf:params:jmap:core"],
              "methodCalls":[
                ["Mailbox/query",{},"c1"],
                ["Core/echo",{
                  "#copied":{"resultOf":"c1","name":"Mailbox/query","path":"/value"}
                },"c2"]
              ]
            }
            """).ConfigureAwait(false);

        const string expected = "before\ufffdmiddle\ufffdafter\ufffd";
        var first = response["methodResponses"]![0]![1]!;
        Assert.AreEqual(expected, first["value"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsTrue(first.AsObject().ContainsKey("invalid\ufffdname"));
        Assert.AreEqual(
            expected,
            response["methodResponses"]![1]![1]!["copied"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ConcurrentRequestLimitRejectsInsteadOfQueuing()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var limiter = fixture.Services.GetRequiredService<JmapConcurrencyLimiter>();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < fixture.Configuration.Jmap.MaxConcurrentRequests; index++)
                leases.Add(await limiter.AcquireRequestAsync(CancellationToken.None).ConfigureAwait(false));

            var exception = await Assert.ThrowsAsync<MailApplicationException>(
                () => limiter.AcquireRequestAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(MailResourceLimit.RequestConcurrency, exception.Failure.Limit);
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
        Assert.AreEqual("2026-09-20T12:34:56.1234567Z", JmapDate.FormatUtc(utc), StringComparer.Ordinal);
        Assert.IsTrue(JmapDate.TryParseDate("2026-09-20T14:34:56.25+02:00", out var offset));
        Assert.AreEqual("2026-09-20T14:34:56.25+02:00", JmapDate.FormatDate(offset), StringComparer.Ordinal);
        Assert.IsTrue(JmapDate.TryParseDate("2026-09-20T23:34:56.25+23:00", out var extendedOffset));
        Assert.AreEqual("2026-09-20T00:34:56.25Z", JmapDate.FormatDate(extendedOffset), StringComparer.Ordinal);
        Assert.IsTrue(JmapDate.TryParseDate("2026-09-20T00:34:56.25-23:00", out extendedOffset));
        Assert.AreEqual("2026-09-20T23:34:56.25Z", JmapDate.FormatDate(extendedOffset), StringComparer.Ordinal);

        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20T12:34:56.000Z", out _));
        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20t12:34:56z", out _));
        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20T14:34:56+02:00", out _));
        Assert.IsFalse(JmapDate.TryParseDate("2026-02-30T12:34:56Z", out _));
        Assert.IsFalse(JmapDate.TryParseDate("2026-09-20T12:34:56+24:00", out _));
        Assert.IsFalse(JmapDate.TryParseDate("2026-09-20T12:34:56+23:60", out _));

        Assert.IsTrue(JmapDate.TryParseUtcDate("1990-12-31T23:59:60Z", out var leapSecond));
        Assert.AreEqual("1991-01-01T00:00:00Z", JmapDate.FormatUtc(leapSecond), StringComparer.Ordinal);
        Assert.IsTrue(JmapDate.TryParseDate("1990-12-31T15:59:60-08:00", out var offsetLeapSecond));
        Assert.AreEqual("1990-12-31T16:00:00-08:00", JmapDate.FormatDate(offsetLeapSecond), StringComparer.Ordinal);
        Assert.IsTrue(JmapDate.TryParseDate("1990-12-31T00:59:60-23:00", out var extendedLeapSecond));
        Assert.AreEqual("1991-01-01T00:00:00Z", JmapDate.FormatDate(extendedLeapSecond), StringComparer.Ordinal);
        Assert.IsFalse(JmapDate.TryParseUtcDate("2026-09-20T12:34:60Z", out _));
    }

    [TestMethod]
    public void SubmissionDatesUseCurrentRfc5322SyntaxAndSemantics()
    {
        Assert.IsTrue(JmapDate.IsValidRfc5322DateTime(
            "Sun, 20 Sep 2026 10:00:00 +0000 (valid)"));
        Assert.IsTrue(JmapDate.IsValidRfc5322DateTime(
            "20 sep 2026 10:00 +9959"));
        Assert.IsTrue(JmapDate.IsValidRfc5322DateTime(
            "31 Dec 2016 23:59:60 -0000"));
        Assert.IsTrue(JmapDate.IsValidRfc5322DateTime(
            "\r\n 20 Sep 12026 10:00:00 +0000"));

        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "20 Sep 26 10:00:00 GMT"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "Mon, 20 Sep 2026 10:00:00 +0000"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "20 (old comment) Sep 2026 10:00:00 +0000"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "20 Sep 2026 1:00:00 +0000"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "20 Sep 2026 10:00:00 +0060"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "20 Sep 2026 10:00:61 +0000"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "20 Sep 1899 10:00:00 +0000"));
        Assert.IsFalse(JmapDate.IsValidRfc5322DateTime(
            "31 Apr 2026 10:00:00 +0000"));
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

        Assert.IsTrue(GatewayDocumentValues.TryGetOptionalInt(
            values, "minimum", 0, out var minimum));
        Assert.AreEqual(GatewayDocumentValues.MinimumInt, minimum);
        Assert.IsTrue(GatewayDocumentValues.TryGetOptionalInt(
            values, "maximum", 0, out var maximum));
        Assert.AreEqual(GatewayDocumentValues.MaximumInt, maximum);
        Assert.IsFalse(GatewayDocumentValues.TryGetOptionalInt(
            values, "tooSmall", 0, out _));
        Assert.IsFalse(GatewayDocumentValues.TryGetOptionalUnsignedInt(
            values, "tooLarge", out _));
        Assert.IsTrue(GatewayDocumentValues.TryGetOptionalUnsignedInt(
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
        Assert.AreEqual(expected, JmapEmailQueryEngine.BaseSubject(subject), StringComparer.Ordinal);
    }

    [TestMethod]
    public void UnicodeCasemapTitlecasesEachCodePointBeforeCompatibilityDecomposition()
    {
        Assert.IsTrue(Rfc5256.CompareUnicodeCasemap("Ǆ", "DZ\u030c") > 0);
        Assert.AreEqual(0, Rfc5256.CompareUnicodeCasemap("café", "CAFE\u0301"));
    }

    [TestMethod]
    public void OptionalNonNullableArgumentsRejectExplicitNull()
    {
        var values = JsonNode.Parse(
            """{"flag":null,"position":null,"size":null,"name":null,"items":null}""")!
            .AsObject();

        Assert.IsFalse(GatewayDocumentValues.TryGetOptionalBoolean(values, "flag", false, out _));
        Assert.IsFalse(GatewayDocumentValues.TryGetOptionalInt(values, "position", 0, out _));
        Assert.IsFalse(GatewayDocumentValues.TryGetOptionalUnsignedInt(
            values,
            "size",
            out _,
            allowNull: false));
        Assert.IsFalse(GatewayDocumentValues.TryGetOptionalString(
            values,
            "name",
            out _,
            allowNull: false));
        Assert.IsFalse(GatewayDocumentValues.TryGetStringArray(values, "items", false, out _));

        Assert.IsTrue(GatewayDocumentValues.TryGetOptionalUnsignedInt(values, "size", out _));
        Assert.IsTrue(GatewayDocumentValues.TryGetOptionalString(values, "name", out _));
        Assert.IsTrue(GatewayDocumentValues.TryGetStringArray(values, "items", true, out _));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The QueryMethodsIgnoreInactiveWindowArguments scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task QueryMethodsIgnoreInactiveWindowArguments()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var emailId = Guid.CreateVersion7();
        var threadId = Guid.CreateVersion7().ToString("N");
        var submissionId = Guid.CreateVersion7();
        var raw = Encoding.ASCII.GetBytes(
            $"Date: Sat, 19 Sep 2026 12:00:00 +0000\r\n" +
            $"From: sender@example.net\r\n" +
            $"To: {fixture.User.Username}\r\n" +
            $"Message-ID: <{emailId:N}@example.net>\r\n" +
            "Subject: Query window\r\n\r\nbody\r\n");
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Emails.AddAsync(new EmailDB
            {
                Id = emailId,
                Sender = "sender@example.net",
                Recipient = fixture.User.Username,
                Subject = "Query window",
                Body = "body\r\n",
                RawMessage = raw,
                SizeBytes = raw.Length,
                EmailObjectId = emailId.ToString("N"),
                ThreadObjectId = threadId,
                MessageId = $"<{emailId:N}@example.net>",
                ReceivedAt = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
                FolderId = fixture.InboxFolderId,
                Uid = 1,
                ModSeq = 1,
            }).ConfigureAwait(false);
            await database.JmapEmailSubmissions.AddAsync(new JmapEmailSubmissionDB
            {
                Id = submissionId,
                SubmissionObjectId = JmapId.Submission(submissionId),
                AccountId = fixture.InboxId,
                IdentityId = JmapId.Identity(fixture.InboxId),
                EmailId = JmapId.Email(emailId),
                ThreadId = JmapId.Thread(threadId),
                QueueId = Guid.CreateVersion7(),
                EnvelopeSender = fixture.User.Username,
                EnvelopeRecipients = ["recipient@example.net"],
                SendAt = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var response = await fixture.InvokeAsync($$$"""
        {
          "using":[
            "{{{GatewayJmapFeatureCodec.CoreCapability}}}",
            "{{{GatewayJmapFeatureCodec.MailCapability}}}",
            "{{{GatewayJmapFeatureCodec.SubmissionCapability}}}"
          ],
          "methodCalls":[
            ["Mailbox/query",{
              "accountId":"{{{fixture.AccountId}}}",
              "anchorOffset":"ignored"
            },"m1"],
            ["Mailbox/query",{
              "accountId":"{{{fixture.AccountId}}}",
              "anchor":"{{{fixture.InboxMailboxId}}}",
              "position":"ignored"
            },"m2"],
            ["Email/query",{
              "accountId":"{{{fixture.AccountId}}}",
              "anchorOffset":"ignored"
            },"e1"],
            ["Email/query",{
              "accountId":"{{{fixture.AccountId}}}",
              "anchor":"{{{JmapId.Email(emailId)}}}",
              "position":"ignored"
            },"e2"],
            ["EmailSubmission/query",{
              "accountId":"{{{fixture.AccountId}}}",
              "anchorOffset":"ignored"
            },"s1"],
            ["EmailSubmission/query",{
              "accountId":"{{{fixture.AccountId}}}",
              "anchor":"{{{JmapId.Submission(submissionId)}}}",
              "position":"ignored"
            },"s2"]
          ]
        }
        """).ConfigureAwait(false);

        var methodResponses = response["methodResponses"]!.AsArray();
        Assert.AreEqual(6, methodResponses.Count);
        foreach (var methodResponse in methodResponses)
            Assert.AreNotEqual("error", methodResponse![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            fixture.InboxMailboxId,
            methodResponses[1]![1]!["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            JmapId.Email(emailId),
            methodResponses[3]![1]!["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            JmapId.Submission(submissionId),
            methodResponses[5]![1]!["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void StringArrayArgumentsPermitRepeatedProjectionProperties()
    {
        var values = JsonNode.Parse("""{"properties":["id","id","name"]}""")!
            .AsObject();

        Assert.IsTrue(GatewayDocumentValues.TryGetStringArray(
            values,
            "properties",
            true,
            out var properties));
        CollectionAssert.AreEqual(
            ExpectedVector3,
            properties!.ToArray());
    }

    [TestMethod]
    public async Task ChangesLimitCountsFoldedObjectIdsRatherThanLogRows()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.JmapChanges.AddRangeAsync(
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
            }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var states = scope.ServiceProvider.GetRequiredService<JmapStateService>();
        var first = await states.GetChangesAsync(
            fixture.InboxId,
            "Test",
            "s0",
            1,
            100,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.IsTrue(first.HasMoreChanges);
        CollectionAssert.AreEqual(ExpectedVector4, first.Created.ToArray());
        Assert.AreEqual(1, first.Created.Count + first.Updated.Count + first.Destroyed.Count);

        var second = await states.GetChangesAsync(
            fixture.InboxId,
            "Test",
            first.NewState,
            1,
            100,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(second);
        Assert.IsFalse(second.HasMoreChanges);
        CollectionAssert.AreEqual(ExpectedVector5, second.Created.ToArray());
    }

    [TestMethod]
    public async Task ChangesRejectUnsafeIntermediateLifecycleBoundaries()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.JmapChanges.AddRangeAsync(
            new JmapChangeDB
            {
                AccountId = fixture.InboxId,
                DataType = "Test",
                ObjectId = "one",
                ChangeKind = JmapConstants.DestroyedChange,
                ChangedAt = DateTime.UtcNow,
            },
            new JmapChangeDB
            {
                AccountId = fixture.InboxId,
                DataType = "Test",
                ObjectId = "two",
                ChangeKind = JmapConstants.UpdatedChange,
                ChangedAt = DateTime.UtcNow,
            },
            new JmapChangeDB
            {
                AccountId = fixture.InboxId,
                DataType = "Test",
                ObjectId = "one",
                ChangeKind = JmapConstants.CreatedChange,
                ChangedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var states = scope.ServiceProvider.GetRequiredService<JmapStateService>();
        var changes = await states.GetChangesAsync(
            fixture.InboxId,
            "Test",
            "s0",
            1,
            100,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(changes);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The BaselineIncludesAllPersistedIdentitiesAndSubmissions scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task BaselineIncludesAllPersistedIdentitiesAndSubmissions()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var defaultIdentityId = fixture.InboxId;
        var additionalIdentityId = Guid.CreateVersion7();
        var submissionId = Guid.CreateVersion7();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.JmapIdentities.AddRangeAsync(
            new JmapIdentityDB
            {
                Id = defaultIdentityId,
                IdentityObjectId = JmapId.Identity(defaultIdentityId),
                AccountId = fixture.InboxId,
                Email = fixture.User.Username,
                MayDelete = false,
            },
            new JmapIdentityDB
            {
                Id = additionalIdentityId,
                IdentityObjectId = JmapId.Identity(additionalIdentityId),
                AccountId = fixture.InboxId,
                Email = fixture.User.Username,
            }).ConfigureAwait(false);
        await database.JmapEmailSubmissions.AddAsync(new JmapEmailSubmissionDB
        {
            Id = submissionId,
            SubmissionObjectId = JmapId.Submission(submissionId),
            AccountId = fixture.InboxId,
            IdentityId = JmapId.Identity(defaultIdentityId),
            EmailId = JmapId.Email(Guid.CreateVersion7()),
            ThreadId = JmapId.Thread(Guid.CreateVersion7().ToString("N")),
            QueueId = Guid.CreateVersion7(),
            EnvelopeSender = fixture.User.Username,
            EnvelopeRecipients = ["recipient@example.net"],
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        database.JmapChanges.RemoveRange(database.JmapChanges.Where(change =>
            change.AccountId == fixture.InboxId
            && (string.Equals(change.DataType, JmapConstants.IdentityDataType
, StringComparison.Ordinal) || string.Equals(change.DataType, JmapConstants.EmailSubmissionDataType, StringComparison.Ordinal))));
        await database.SaveChangesAsync().ConfigureAwait(false);

        var states = scope.ServiceProvider.GetRequiredService<JmapStateService>();
        var identityChanges = await states.GetChangesAsync(
            fixture.InboxId,
            JmapConstants.IdentityDataType,
            "s0",
            null,
            100,
            CancellationToken.None).ConfigureAwait(false);
        var submissionChanges = await states.GetChangesAsync(
            fixture.InboxId,
            JmapConstants.EmailSubmissionDataType,
            "s0",
            null,
            100,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(identityChanges);
        CollectionAssert.AreEquivalent(
            new[] { JmapId.Identity(defaultIdentityId), JmapId.Identity(additionalIdentityId) },
            identityChanges.Created.ToArray());
        Assert.IsNotNull(submissionChanges);
        CollectionAssert.AreEqual(
            new[] { JmapId.Submission(submissionId) },
            submissionChanges.Created.ToArray());
    }

    [TestMethod]
    public async Task ChangesRequirePositiveLimitButQueryChangesAcceptsZero()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(
            $$$$"""
            {
              "using":[
                "urn:ietf:params:jmap:core",
                "urn:ietf:params:jmap:mail",
                "urn:ietf:params:jmap:submission"
              ],
              "methodCalls":[
                ["Mailbox/get",{"accountId":"{{{{fixture.AccountId}}}}","ids":[]},"g1"],
                ["Mailbox/changes",{
                  "accountId":"{{{{fixture.AccountId}}}}",
                  "#sinceState":{"resultOf":"g1","name":"Mailbox/get","path":"/state"},
                  "maxChanges":0
                },"c1"],
                ["Mailbox/query",{"accountId":"{{{{fixture.AccountId}}}}"},"q1"],
                ["Mailbox/queryChanges",{
                  "accountId":"{{{{fixture.AccountId}}}}",
                  "#sinceQueryState":{"resultOf":"q1","name":"Mailbox/query","path":"/queryState"},
                  "maxChanges":0
                },"qc1"],
                ["Email/query",{"accountId":"{{{{fixture.AccountId}}}}"},"q2"],
                ["Email/queryChanges",{
                  "accountId":"{{{{fixture.AccountId}}}}",
                  "#sinceQueryState":{"resultOf":"q2","name":"Email/query","path":"/queryState"},
                  "maxChanges":0
                },"qc2"],
                ["EmailSubmission/query",{"accountId":"{{{{fixture.AccountId}}}}"},"q3"],
                ["EmailSubmission/queryChanges",{
                  "accountId":"{{{{fixture.AccountId}}}}",
                  "#sinceQueryState":{
                    "resultOf":"q3",
                    "name":"EmailSubmission/query",
                    "path":"/queryState"
                  },
                  "maxChanges":0
                },"qc3"]
              ]
            }
            """).ConfigureAwait(false);

        var methodResponses = response["methodResponses"]!.AsArray();
        Assert.AreEqual("error", methodResponses[1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidArguments",
            methodResponses[1]![1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Mailbox/queryChanges", methodResponses[3]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, methodResponses[3]![1]!["added"]!.AsArray().Count);
        Assert.AreEqual("Email/queryChanges", methodResponses[5]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, methodResponses[5]![1]!["added"]!.AsArray().Count);
        Assert.AreEqual(
            "EmailSubmission/queryChanges",
            methodResponses[7]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0, methodResponses[7]![1]!["added"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task TypedIdArgumentsRejectValuesOutsideTheIdAlphabet()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(
            """
            {
              "using":[
                "urn:ietf:params:jmap:core",
                "urn:ietf:params:jmap:mail",
                "urn:ietf:params:jmap:submission"
              ],
              "methodCalls":[
                ["Email/get",{"accountId":"ACCOUNT","ids":["not an id"]},"c1"],
                ["Mailbox/query",{"accountId":"ACCOUNT","anchor":"not/an/id"},"c2"],
                ["EmailSubmission/query",{"accountId":"ACCOUNT",
                  "filter":{"identityIds":["not an id"]}},"c3"]
              ]
            }
            """.Replace("ACCOUNT", JmapId.Account(fixture.InboxId), StringComparison.Ordinal)).ConfigureAwait(false);

        Assert.AreEqual(
            "invalidArguments",
            response["methodResponses"]?[0]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidArguments",
            response["methodResponses"]?[1]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(
            "invalidArguments",
            response["methodResponses"]?[2]?[1]?["type"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task SetAndImportMethodsRejectMalformedTypedIds()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(
            $$$$"""
            {
              "using":[
                "urn:ietf:params:jmap:core",
                "urn:ietf:params:jmap:mail",
                "urn:ietf:params:jmap:submission",
                "urn:ietf:params:jmap:vacationresponse"
              ],
              "methodCalls":[
                ["Email/set",{"accountId":"{{{{fixture.AccountId}}}}",
                  "update":{"not an id":{}}},"c1"],
                ["Mailbox/set",{"accountId":"{{{{fixture.AccountId}}}}",
                  "destroy":["not/an/id"]},"c2"],
                ["Email/import",{"accountId":"{{{{fixture.AccountId}}}}",
                  "emails":{"not an id":{}}},"c3"],
                ["Identity/set",{"accountId":"{{{{fixture.AccountId}}}}",
                  "destroy":["not an id"]},"c4"],
                ["EmailSubmission/set",{"accountId":"{{{{fixture.AccountId}}}}",
                  "update":{"not an id":{}}},"c5"],
                ["VacationResponse/set",{"accountId":"{{{{fixture.AccountId}}}}",
                  "update":{"not an id":{}}},"c6"],
                ["PushSubscription/set",{"create":{"not an id":{}}},"c7"]
              ]
            }
            """).ConfigureAwait(false);

        var methodResponses = response["methodResponses"]!.AsArray();
        Assert.AreEqual(7, methodResponses.Count);
        foreach (var methodResponse in methodResponses)
        {
            Assert.AreEqual("error", methodResponse![0]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.AreEqual(
                "invalidArguments",
                methodResponse[1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        }
    }

    [TestMethod]
    public async Task VacationMethodsEnforceAdvertisedObjectLimits()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var excessiveCount = fixture.Configuration.Jmap.MaxObjectsInGet + 1;
        var ids = new JsonArray(
            Enumerable.Range(0, excessiveCount)
                .Select(index => JsonValue.Create($"id-{index}"))
                .ToArray());
        var create = new JsonObject();
        foreach (var index in Enumerable.Range(0, fixture.Configuration.Jmap.MaxObjectsInSet + 1))
            create[$"create-{index}"] = new JsonObject();

        var response = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(
                GatewayJmapFeatureCodec.CoreCapability,
                GatewayJmapFeatureCodec.VacationResponseCapability),
            ["methodCalls"] = new JsonArray(
                new JsonArray(
                    "VacationResponse/get",
                    new JsonObject
                    {
                        ["accountId"] = fixture.AccountId,
                        ["ids"] = ids,
                    },
                    "g1"),
                new JsonArray(
                    "VacationResponse/set",
                    new JsonObject
                    {
                        ["accountId"] = fixture.AccountId,
                        ["create"] = create,
                    },
                    "s1")),
        }).ConfigureAwait(false);

        var methodResponses = response["methodResponses"]!.AsArray();
        Assert.AreEqual("requestTooLarge", methodResponses[0]![1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("requestTooLarge", methodResponses[1]![1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private sealed class ImplicitResponseMethod : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(
            JmapInvocationContext context,
            JsonObject arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new JmapMethodResponse(
                Operation,
                new JsonObject { ["value"] = "primary" },
                [new JmapMethodResponse(
                    MailOperationKind.ReadMessages,
                    new JsonObject { ["value"] = "additional" })]));
    }

    private sealed class InvalidUnicodeResponseMethod : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(
            JmapInvocationContext context,
            JsonObject arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new JmapMethodResponse(
                Operation,
                new JsonObject
                {
                    ["value"] = "before\ufdd0middle\U0001fffeafter\ud800",
                    ["invalid\ufdefname"] = true,
                }));
    }

    private sealed class InvocationProbeMethod(Action invoked) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(
            JmapInvocationContext context,
            JsonObject arguments,
            CancellationToken cancellationToken)
        {
            invoked();
            return Task.FromResult(new JmapMethodResponse(Operation, new JsonObject()));
        }
    }

    private sealed class AtomicityProbeMethod(
        MailOperationKind operation,
        string creationId,
        string objectId,
        Action postCommit,
        bool fail) : IJmapMethod
    {
        public MailOperationKind Operation => operation;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(
            JmapInvocationContext context,
            JsonObject arguments,
            CancellationToken cancellationToken)
        {
            context.CreatedIds[creationId] = objectId;
            context.AddPostCommitAction(_ =>
            {
                postCommit();
                return Task.CompletedTask;
            });
            if (fail)
                throw new InvalidOperationException("Atomicity probe failure.");
            return Task.FromResult(new JmapMethodResponse(Operation, new JsonObject()));
        }
    }

    private sealed class PostCommitCancellationProbeMethod(
        CancellationTokenSource cancellation,
        Action completed) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(
            JmapInvocationContext context,
            JsonObject arguments,
            CancellationToken cancellationToken)
        {
            context.AddPostCommitAction(async _ =>
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            });
            context.AddPostCommitAction(postCommitCancellationToken =>
            {
                postCommitCancellationToken.ThrowIfCancellationRequested();
                completed();
                return Task.CompletedTask;
            });
            return Task.FromResult(new JmapMethodResponse(Operation, new JsonObject()));
        }
    }
    private static readonly string[] ExpectedVector1 = new[] { "a", "b" };
    private static readonly string[] ExpectedVector2 = new[] { "Email", "Mailbox" };
    private static readonly string[] ExpectedVector3 = new[] { "id", "id", "name" };
    private static readonly string[] ExpectedVector4 = new[] { "one" };
    private static readonly string[] ExpectedVector5 = new[] { "two" };
}
