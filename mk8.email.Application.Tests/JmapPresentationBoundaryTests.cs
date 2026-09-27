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
public sealed class JmapPresentationBoundaryTests
{
    [TestMethod]
    public async Task WorkerRejectsMalformedTypedBatchBeforeAnyInvocation()
    {
        var count = 0;
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(() => count++)));
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.FindFolders, new JsonObject(), "first"),
             new JmapApplicationCall(MailOperationKind.Echo, null!, "second")]);
        var exception = await Assert.ThrowsAsync<MailApplicationException>(() => processor.ProcessAsync(batch, fixture.User));

        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task WorkerValidatesTypedCreationIdentifiersIndependently()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var batch = new JmapApplicationBatch([MailFeature.Basic], [],
            new Dictionary<string, string> { ["invalid key"] = "object-id" });
        var exception = await Assert.ThrowsAsync<MailApplicationException>(() => processor.ProcessAsync(batch, fixture.User));

        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);
    }

    [TestMethod]
    public async Task InvalidCredentialsTakePriorityOverInvalidJson()
    {
        await using var fixture = await DavFixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jmap/api")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.PrimaryAddress + ":incorrect-password")));
        using var response = await fixture.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("notJSON", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CapacityFailureTakesPriorityOverInvalidJson()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var limiter = fixture.Services.GetRequiredService<JmapConcurrencyLimiter>();
        var configuration = fixture.Services.GetRequiredService<EnvironmentConfig>();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < configuration.Jmap.MaxConcurrentRequests; index++)
                leases.Add(await limiter.AcquireRequestAsync(CancellationToken.None));
            using var response = await fixture.SendAsync("POST", "/jmap/api", "{", "application/json");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("maxConcurrentRequests", body?["limit"]?.GetValue<string>());
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
        await using var fixture = await DavFixture.CreateAsync();
        using var response = await fixture.SendAsync("POST", "/jmap/api",
            """{"using":["urn:example:unsupported"],"methodCalls":[null]}""", "application/json");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("urn:ietf:params:jmap:error:unknownCapability", body?["type"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task CallLimitTakesPriorityOverMalformedInvocation()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var count = fixture.Services.GetRequiredService<EnvironmentConfig>().Jmap.MaxCallsInRequest + 1;
        var calls = new JsonArray();
        for (var index = 0; index < count; index++)
            calls.Add((JsonNode?)null);
        var document = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core"),
            ["methodCalls"] = calls,
        };
        using var response = await fixture.SendAsync("POST", "/jmap/api", document.ToJsonString(), "application/json");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("maxCallsInRequest", body?["limit"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task MalformedTailDoesNotExecuteEarlierValidMutation()
    {
        await using var fixture = await DavFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.Folders.CountAsync();
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
        using var response = await fixture.SendAsync("POST", "/jmap/api", document.ToJsonString(), "application/json");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", body?["type"]?.GetValue<string>());
        Assert.AreEqual(before, await database.Folders.CountAsync());
    }

    [TestMethod]
    public async Task AuthenticatedInvalidIJsonIsRenderedByGateway()
    {
        await using var fixture = await DavFixture.CreateAsync();
        foreach (var document in new[] { "{", "{\"using\":[],\"using\":[]}", "{\"n\":1e999}", "{\"s\":\"\\uD800\"}" })
        {
            using var response = await fixture.SendAsync("POST", "/jmap/api", document, "application/json");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, document);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("urn:ietf:params:jmap:error:notJSON", body?["type"]?.GetValue<string>(), document);
            Assert.AreEqual(400, body?["status"]?.GetValue<int>());
        }
    }

    private sealed class ProbeMethod(Action onInvoke) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;

        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken = default)
        {
            onInvoke();
            return Task.FromResult(new JmapMethodResponse(Operation, new JsonObject()));
        }
    }
}
