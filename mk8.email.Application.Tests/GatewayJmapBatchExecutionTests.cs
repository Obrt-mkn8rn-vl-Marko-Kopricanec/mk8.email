using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayJmapBatchExecutionTests
{
    [TestMethod]
    public void WholeBatchLeaseUsesTheLowerLimitAndReleasesExactlyOnce()
    {
        var limiter = new GatewayJmapBatchLimiter(new EnvironmentConfig { Jmap = new JmapConfig { MaxConcurrentRequests = 2 } });
        var first = limiter.TryAcquire(1);
        Assert.IsNotNull(first);
        Assert.IsNull(limiter.TryAcquire(1));
        first.Dispose();
        first.Dispose();
        using var second = limiter.TryAcquire(4);
        using var third = limiter.TryAcquire(4);
        Assert.IsNotNull(second);
        Assert.IsNotNull(third);
        Assert.IsNull(limiter.TryAcquire(4));
    }

    [TestMethod]
    public async Task GatewayCapacityStillPrecedesLateParsingAndFeatureFailuresButNotAuthentication()
    {
        await using var fixture = await DavFixture.CreateAsync();
        var limiter = fixture.Services.GetRequiredService<GatewayJmapBatchLimiter>();
        var leases = new List<IDisposable>();
        try
        {
            while (limiter.TryAcquire(int.MaxValue) is { } lease)
                leases.Add(lease);
            using var response = await fixture.SendAsync("POST", "/jmap/api", "{", "application/json");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("maxConcurrentRequests", problem!["limit"]!.GetValue<string>());
            using var featureResponse = await fixture.SendAsync("POST", "/jmap/api",
                """{"using":["unsupported"],"methodCalls":[]}""", "application/json");
            Assert.AreEqual("maxConcurrentRequests", JsonNode.Parse(await featureResponse.Content.ReadAsStringAsync())!["limit"]!.GetValue<string>());
            using var invalid = new HttpRequestMessage(HttpMethod.Post, "/jmap/api")
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json"),
            };
            invalid.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.PrimaryAddress + ":bad-password")));
            using var authResponse = await fixture.Client.SendAsync(invalid);
            Assert.AreEqual(HttpStatusCode.Unauthorized, authResponse.StatusCode);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [TestMethod]
    public async Task ExpiredWholeBatchDoesNotExecuteEvenPresentationOnlyCalls()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<mk8.email.Jmap.JmapRequestProcessor>();
        using var deadline = GatewayApplicationDeadline.Begin(TimeSpan.FromSeconds(-1));
        var error = await Assert.ThrowsExactlyAsync<GatewayApplicationException>(() =>
            JmapFixture.ProcessBatchAsync(processor, new([MailFeature.Basic],
                [new(MailOperationKind.Echo, new JsonObject { ["text"] = "late" }, "one")]), fixture.User));
        Assert.AreEqual("application-timeout", error.Code);
    }
}
