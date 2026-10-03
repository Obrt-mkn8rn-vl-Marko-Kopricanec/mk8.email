using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapProfileBoundaryTests
{
    [TestMethod]
    public async Task WorkerProfileDoesNotParseItsPublicUrlOrExposePresentationFields()
    {
        var fixture = (await SplitWorkerAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>()
            .GetProfileAsync(fixture.User).ConfigureAwait(false);
        var value = JsonSerializer.SerializeToNode(profile, SerializationOptions1)!;

        Assert.AreEqual(fixture.User.Username, profile.Username, StringComparer.Ordinal);
        Assert.IsFalse(value.ToJsonString().Contains("worker.internal", StringComparison.Ordinal));
        Assert.IsFalse(value.AsObject().ContainsKey("apiUrl"));
        Assert.IsFalse(value.AsObject().ContainsKey("state"));
        Assert.AreEqual(77, profile.Limits.MaxObjectsInGet);
    }

    [TestMethod]
    public async Task DiscoveryAndBatchStatesAgreeAcrossDifferentHostConfigurations()
    {
        var fixture = (await SplitWorkerAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>()
            .GetProfileAsync(fixture.User).ConfigureAwait(false);
        var gateway = Gateway("https://edge.example.test/mail");
        var session = GatewayJmapProfileCodec.Render(profile, gateway);
        var batch = await JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(),
            new JmapApplicationBatch([MailFeature.Basic],
                [new JmapApplicationCall(MailOperationKind.Echo, new JsonObject { ["ok"] = true }, "one")]), fixture.User).ConfigureAwait(false);
        var response = GatewayJmapBatchCodec.Render(batch, gateway);

        Assert.AreEqual("https://edge.example.test/mail/jmap/api", session["apiUrl"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(session["state"]!.GetValue<string>(), response["sessionState"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsTrue(response["methodResponses"]![0]![1]!["ok"]!.GetValue<bool>());
        var other = GatewayJmapProfileCodec.Render(profile, Gateway("https://replacement.example.test"));
        Assert.AreNotEqual(session["state"]!.GetValue<string>(), other["state"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task AdvertisedLimitsRespectBothGatewayAndWorkerPolicies()
    {
        var fixture = (await SplitWorkerAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>()
            .GetProfileAsync(fixture.User).ConfigureAwait(false);
        var gateway = new EnvironmentConfig
        {
            Jmap = new JmapConfig { PublicBaseUrl = "https://edge.example.test", MaxRequestSizeBytes = 1024, MaxUploadSizeBytes = 2048 },
        };
        var core = GatewayJmapProfileCodec.Render(profile, gateway)["capabilities"]![GatewayJmapFeatureCodec.CoreCapability]!;

        Assert.AreEqual(1024L, core["maxSizeRequest"]!.GetValue<long>());
        Assert.AreEqual(2048L, core["maxSizeUpload"]!.GetValue<long>());
        Assert.AreEqual(77, core["maxObjectsInGet"]!.GetValue<int>());
        var workerLimited = profile with { Limits = profile.Limits with { MaxRequestSizeBytes = 512 } };
        Assert.AreEqual(512L, GatewayJmapProfileCodec.Render(workerLimited, gateway)
            ["capabilities"]![GatewayJmapFeatureCodec.CoreCapability]!["maxSizeRequest"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task ProfileTransportAndStateHashPreserveMetadataAndPolicyChanges()
    {
        var fixture = (await SplitWorkerAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<JmapAccountProfileService>()
            .GetProfileAsync(fixture.User).ConfigureAwait(false);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var transported = JsonSerializer.Deserialize<JmapApplicationProfile>(JsonSerializer.Serialize(profile, options), options)!;
        var gateway = Gateway("https://edge.example.test");
        var rendered = GatewayJmapProfileCodec.Render(profile, gateway);

        Assert.IsTrue(JsonNode.DeepEquals(rendered, GatewayJmapProfileCodec.Render(transported, gateway)));
        var changedPolicy = profile with { Limits = profile.Limits with { MaxCallsInRequest = 5 } };
        var changedAccount = profile with { Accounts = [profile.Accounts[0] with { Name = "renamed@example.test" }] };
        Assert.AreNotEqual(rendered["state"]!.GetValue<string>(), GatewayJmapProfileCodec.Render(changedPolicy, gateway)["state"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreNotEqual(rendered["state"]!.GetValue<string>(), GatewayJmapProfileCodec.Render(changedAccount, gateway)["state"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void TypedChangeDataRoundTripsAndGatewayFramesEventsAndVerification()
    {
        var changes = new JmapApplicationChanges(new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["account-id"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["Mailbox"] = "state-1", ["Email"] = "state-2" },
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var transported = JsonSerializer.Deserialize<JmapApplicationChanges>(JsonSerializer.Serialize(changes, options), options)!;
        var rendered = GatewayJmapChangesCodec.Render(transported);

        Assert.AreEqual("StateChange", rendered["@type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("state-1", rendered["changed"]!["account-id"]!["Mailbox"]!.GetValue<string>(), StringComparer.Ordinal);
        var push = JsonNode.Parse(GatewayJmapChangesCodec.EncodePush(new JmapPushMessage(Changes: transported)));
        Assert.IsTrue(JsonNode.DeepEquals(rendered, push));
        var verification = JsonNode.Parse(GatewayJmapChangesCodec.EncodePush(new JmapPushMessage("push-id", "code")))!;
        Assert.AreEqual("PushVerification", verification["@type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("push-id", verification["pushSubscriptionId"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("code", verification["verificationCode"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.ThrowsExactly<ArgumentException>(() => GatewayJmapChangesCodec.EncodePush(new JmapPushMessage()));
        Assert.ThrowsExactly<ArgumentException>(() => GatewayJmapChangesCodec.EncodePush(new JmapPushMessage("push-id", "code", changes)));
    }

    private static Task<JmapFixture> SplitWorkerAsync() => JmapFixture.CreateAsync(configureServices: services =>
        services.AddSingleton(new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "worker.internal" },
            Jmap = new JmapConfig { PublicBaseUrl = "not a usable URI", MaxObjectsInGet = 77 },
        }));

    private static EnvironmentConfig Gateway(string publicBase) => new()
    {
        Smtp = new SmtpConfig { Hostname = "edge.example.test" },
        Jmap = new JmapConfig { PublicBaseUrl = publicBase },
    };
    private static readonly JsonSerializerOptions SerializationOptions1 = new JsonSerializerOptions(JsonSerializerDefaults.Web);
}
