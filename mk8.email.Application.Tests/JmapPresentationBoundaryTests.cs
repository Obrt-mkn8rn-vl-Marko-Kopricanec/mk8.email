using mk8.email.Gateway.Protocols.Jmap;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapPresentationBoundaryTests
{
    [TestMethod]
    public async Task GatewayRejectsMalformedTypedBatchBeforeAnyInvocation()
    {
        var count = 0;
        var fixture = (await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(() => count++))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.FindFolders, new JsonObject(), "first"),
             new JmapApplicationCall(MailOperationKind.Echo, null!, "second")]);
        var exception = await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() => JmapFixture.ProcessBatchAsync(processor, batch, fixture.User)).ConfigureAwait(false);

        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", exception.Problem.Type, StringComparer.Ordinal);
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task GatewayValidatesTypedCreationIdentifiersBeforeSequencing()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var batch = new JmapApplicationBatch([MailFeature.Basic], [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["invalid key"] = "object-id" });
        var exception = await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() => JmapFixture.ProcessBatchAsync(processor, batch, fixture.User)).ConfigureAwait(false);

        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", exception.Problem.Type, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task InvalidCredentialsTakePriorityOverInvalidJson()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jmap/api")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(DavFixture.PrimaryAddress + ":incorrect-password")));
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("notJSON", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CapacityFailureTakesPriorityOverInvalidJson()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var limiter = fixture.Services.GetRequiredService<JmapConcurrencyLimiter>();
        var configuration = fixture.Services.GetRequiredService<EnvironmentConfig>();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < configuration.Jmap.MaxConcurrentRequests; index++)
                leases.Add(await limiter.AcquireRequestAsync(CancellationToken.None).ConfigureAwait(false));
            using var response = await fixture.SendAsync("POST", "/jmap/api", "{", "application/json").ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            Assert.AreEqual("maxConcurrentRequests", body?["limit"]?.GetValue<string>(), StringComparer.Ordinal);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [TestMethod]
    public async Task UnsupportedCapabilityTakesPriorityOverMalformedInvocation()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var response = await fixture.SendAsync("POST", "/jmap/api",
            """{"using":["urn:example:unsupported"],"methodCalls":[null]}""", "application/json").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("urn:ietf:params:jmap:error:unknownCapability", body?["type"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task CallLimitTakesPriorityOverMalformedInvocation()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var count = fixture.Services.GetRequiredService<EnvironmentConfig>().Jmap.MaxCallsInRequest + 1;
        var calls = new JsonArray();
        for (var index = 0; index < count; index++)
            calls.Add((JsonNode?)null);
        var document = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core"),
            ["methodCalls"] = calls,
        };
        using var response = await fixture.SendAsync("POST", "/jmap/api", document.ToJsonString(), "application/json").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("maxCallsInRequest", body?["limit"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task MalformedTailDoesNotExecuteEarlierValidMutation()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.Folders.CountAsync().ConfigureAwait(false);
        var document = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail"),
            ["methodCalls"] = new JsonArray(
                new JsonArray("Mailbox/set", new JsonObject
                {
                    ["accountId"] = fixture.AccountId,
                    ["create"] = new JsonObject { ["new"] = new JsonObject { ["name"] = "MustNotExist" } },
                }, "first"),
                new JsonArray("Core/echo", new JsonObject())),
        };
        using var response = await fixture.SendAsync("POST", "/jmap/api", document.ToJsonString(), "application/json").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", body?["type"]?.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(before, await database.Folders.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AuthenticatedInvalidIJsonIsRenderedByGateway()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        foreach (var document in new[] { "{", "{\"using\":[],\"using\":[]}", "{\"n\":1e999}", "{\"s\":\"\\uD800\"}" })
        {
            using var response = await fixture.SendAsync("POST", "/jmap/api", document, "application/json").ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, document);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            Assert.AreEqual("urn:ietf:params:jmap:error:notJSON", body?["type"]?.GetValue<string>(), StringComparer.Ordinal, document);
            Assert.AreEqual(400, body?["status"]?.GetValue<int>());
        }
    }

    private sealed class ProbeMethod(Action onInvoke) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken)
        {
            onInvoke();
            return Task.FromResult(new JmapMethodResponse(Operation, new JsonObject()));
        }
    }
}
