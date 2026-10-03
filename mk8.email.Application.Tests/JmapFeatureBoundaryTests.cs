using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapFeatureBoundaryTests
{
    [TestMethod]
    [DataRow("urn:ietf:params:jmap:core", MailFeature.Basic, 1)]
    [DataRow("urn:ietf:params:jmap:mail", MailFeature.Messages, 2)]
    [DataRow("urn:ietf:params:jmap:submission", MailFeature.Submission, 3)]
    [DataRow("urn:ietf:params:jmap:vacationresponse", MailFeature.AutomaticReplies, 4)]
    [DataRow("urn:ietf:params:jmap:contacts", MailFeature.Contacts, 5)]
    public void GatewayOwnsCapabilityNamesAndStableFeatureNumbers(string name, MailFeature feature, int number)
    {
        Assert.AreEqual(number, (int)feature);
        Assert.AreEqual(feature, GatewayJmapFeatureCodec.Decode(name));
        Assert.AreEqual(MailFeature.Unsupported, GatewayJmapFeatureCodec.Decode(name.ToUpperInvariant()));
        Assert.AreEqual(MailFeature.Unsupported, GatewayJmapFeatureCodec.Decode(name + " "));
    }

    [TestMethod]
    public async Task TransportContainsOnlyTypedFeatureIdentifiersAndPreservesOpaqueValues()
    {
        var batch = GatewayJmapBatchCodec.Parse(JsonNode.Parse("""
            {"using":["urn:ietf:params:jmap:core","urn:example:unsupported"],"methodCalls":[
              ["Core/echo",{"capability":"urn:example:opaque","Features":[7],"type":"urn:example:opaque"},"one"]]}
            """), out var preflight);
        CollectionAssert.AreEqual(new[] { MailFeature.Basic, MailFeature.Unsupported }, batch.Features);
        Assert.IsNotNull(preflight);
        CollectionAssert.AreEqual(batch.Features, preflight.Features.ToArray());
        var options = ContractOptions();
        var encoded = JsonSerializer.SerializeToNode(batch, options)!;
        Assert.IsNull(encoded["capabilities"]);
        Assert.AreEqual(1, encoded["features"]![0]!.GetValue<int>());
        Assert.AreEqual(0, encoded["features"]![1]!.GetValue<int>());
        var transported = JsonSerializer.Deserialize<JmapApplicationBatch>(encoded, options)!;
        var accepted = transported with { Features = [MailFeature.Basic, MailFeature.Basic] };
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var result = await JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), accepted, fixture.User).ConfigureAwait(false);
        Assert.AreEqual("urn:example:opaque", result.Invocations[0].Arguments["capability"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(7, result.Invocations[0].Arguments["Features"]![0]!.GetValue<int>());
        Assert.AreEqual("urn:example:opaque", result.Invocations[0].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("{\"capabilities\":[\"urn:ietf:params:jmap:core\"],\"invocations\":[]}")]
    [DataRow("{\"Features\":[1],\"invocations\":[]}")]
    public void OldOrWrongCaseBatchFieldsAreNotSilentlyDefaulted(string json)
    {
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<JmapApplicationBatch>(json, ContractOptions()));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<MailAdmissionPlan>(json, ContractOptions()));
    }

    [TestMethod]
    public void PresentationOnlyProblemsCannotBecomeDomainFailures()
    {
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<MailApplicationFailure>(
            """{"type":"urn:ietf:params:jmap:error:limit","title":"Limit","limit":"maxCallsInRequest"}""", ContractOptions()));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<MailApplicationFailure>(
            """{"Kind":3,"limit":1}""", ContractOptions()));
    }

    [TestMethod]
    public async Task ExplicitNullFeaturesAreRejectedForBatchesAndPreflight()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var batch = JsonSerializer.Deserialize<JmapApplicationBatch>(
            """{"features":null,"invocations":[]}""", ContractOptions())!;
        var error = await Assert.ThrowsAsync<MailApplicationException>(() => JmapFixture.ProcessBatchAsync(processor, batch, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, error.Failure.Kind);
        var preflight = JsonSerializer.Deserialize<MailAdmissionPlan>(
            """{"features":null,"invocationCount":0}""", ContractOptions())!;
        var preflightError = Assert.ThrowsExactly<MailApplicationException>(() => processor.ValidatePlan(preflight));
        Assert.AreEqual(MailFailureKind.MalformedBatch, preflightError.Failure.Kind);
    }

    [TestMethod]
    public void NeutralAdmissionPlanPreservesExistingInternalWireShape()
    {
        var options = ContractOptions();
        var plan = new MailAdmissionPlan([MailFeature.Basic, MailFeature.Contacts], 3);
        var encoded = JsonSerializer.SerializeToNode(plan, options)!;
        Assert.AreEqual(3, encoded["invocationCount"]!.GetValue<int>());
        Assert.IsNull(encoded["operationCount"]);
        var decoded = JsonSerializer.Deserialize<MailAdmissionPlan>(encoded, options)!;
        Assert.AreEqual(3, decoded.OperationCount);
        CollectionAssert.AreEqual(plan.Features.ToArray(), decoded.Features.ToArray());
    }

    [TestMethod]
    [DataRow((MailFeature)(-1))]
    [DataRow((MailFeature)999)]
    public async Task UndefinedFeaturesRejectTheWholeBatchBeforeAnyInvocation(MailFeature feature)
    {
        var calls = 0;
        var fixture = (await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(MailFeature.Basic, () => calls++))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var batch = new JmapApplicationBatch([MailFeature.Basic, feature],
            [new(MailOperationKind.FindFolders, new JsonObject(), "first")]);
        var error = await Assert.ThrowsAsync<MailApplicationException>(() =>
            JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, error.Failure.Kind);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(MailFeature.Unsupported)]
    [DataRow((MailFeature)999)]
    public async Task InvalidRegisteredFeaturesFailBeforeDispatch(MailFeature feature)
    {
        var fixture = (await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(feature, () => Assert.Fail("No method should run.")))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        Assert.ThrowsExactly<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>());
    }

    [TestMethod]
    public async Task EveryRegisteredHandlerUsesAValidDomainFeature()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var methods = scope.ServiceProvider.GetServices<IJmapMethod>().ToArray();
        Assert.IsEmpty(methods);
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailFolderReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailFolderMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailChangesReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailAddressBookReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailAddressBookMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailIdentityReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailIdentityMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailVacationReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailPushSubscriptionReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailPushSubscriptionMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailThreadReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailSubmissionReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailBlobCopyService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailVacationMutator>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailSubmissionQueryService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailFolderQueryService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailMessageQueryService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailMessageProjectionService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailMessageMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailSubmissionMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailSearchSnippetService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailContactCopyService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailContactQueryService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailContactReader>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailContactMutationService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailImportService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IMailCopyService>());
        Assert.IsTrue(methods.All(method => Enum.IsDefined(method.Feature) && method.Feature != MailFeature.Unsupported));
        CollectionAssert.AreEquivalent(Enum.GetValues<MailFeature>().Except([MailFeature.Unsupported]).ToArray(),
            new[] { MailFeature.AutomaticReplies, MailFeature.Basic, MailFeature.Messages,
                MailFeature.Contacts, MailFeature.Submission });
    }

    [TestMethod]
    public async Task TypedMailAndSubmissionCapabilitiesDoNotDependOnLegacyHandlers()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var processor = new JmapRequestProcessor([],
            services.GetRequiredService<JmapAccountProfileService>(),
            services.GetRequiredService<EmailDbContext>(),
            services.GetRequiredService<EnvironmentConfig>(),
            services.GetRequiredService<LargeObjectTransactionEffects>(),
            NullLogger<JmapRequestProcessor>.Instance);
        processor.ValidatePlan(new([MailFeature.Basic, MailFeature.Messages, MailFeature.Submission], 0));
    }

    [TestMethod]
    [DataRow(MailFailureKind.MalformedBatch, MailResourceLimit.None, "notRequest", "Invalid JMAP request", null)]
    [DataRow(MailFailureKind.UnsupportedFeature, MailResourceLimit.None, "unknownCapability", "Unknown capability", null)]
    [DataRow(MailFailureKind.InvalidSelection, MailResourceLimit.None, "invalidArguments", "Invalid event source parameters", null)]
    [DataRow(MailFailureKind.ResourceLimit, MailResourceLimit.OperationCount, "limit", "Request limit exceeded", "maxCallsInRequest")]
    [DataRow(MailFailureKind.ResourceLimit, MailResourceLimit.RequestConcurrency, "limit", "Request limit exceeded", "maxConcurrentRequests")]
    [DataRow(MailFailureKind.ResourceLimit, MailResourceLimit.UploadConcurrency, "limit", "Request limit exceeded", "maxConcurrentUpload")]
    [DataRow(MailFailureKind.ResourceLimit, MailResourceLimit.UploadSize, "limit", "Upload failed", "maxSizeUpload")]
    public void GatewayRendersEveryTypedFailure(MailFailureKind kind, MailResourceLimit limit, string type, string title, string? name)
    {
        var original = new MailApplicationFailure(kind, "Opaque detail.", limit);
        var transported = JsonSerializer.Deserialize<MailApplicationFailure>(JsonSerializer.Serialize(original, ContractOptions()), ContractOptions())!;
        Assert.AreEqual(original, transported);
        var problem = GatewayJmapFailureCodec.Render(transported);
        Assert.AreEqual("urn:ietf:params:jmap:error:" + type, problem.Type, StringComparer.Ordinal);
        Assert.AreEqual(title, problem.Title, StringComparer.Ordinal);
        Assert.AreEqual(name, problem.Limit, StringComparer.Ordinal);
        Assert.AreEqual(original.Detail, problem.Detail, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(MailFailureKind.None, MailResourceLimit.None)]
    [DataRow((MailFailureKind)999, MailResourceLimit.None)]
    [DataRow(MailFailureKind.ResourceLimit, MailResourceLimit.None)]
    [DataRow(MailFailureKind.ResourceLimit, (MailResourceLimit)999)]
    [DataRow(MailFailureKind.MalformedBatch, MailResourceLimit.OperationCount)]
    [DataRow(MailFailureKind.UnsupportedFeature, MailResourceLimit.UploadSize)]
    public void InvalidTypedFailuresCannotBeRendered(MailFailureKind kind, MailResourceLimit limit)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayJmapFailureCodec.Render(new MailApplicationFailure(kind, Limit: limit)));
    }

    [TestMethod]
    public async Task WorkerEnforcesFeatureAndCountPolicyWithoutPresentationValues()
    {
        var fixture = (await CreateAuthenticatedFixtureAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IJmapApplicationService>();
        var authentication = Authentication(fixture, "good");
        var unsupported = await service.ValidatePlanAsync(new(authentication, new([MailFeature.Unsupported], -1))).ConfigureAwait(false);
        Assert.AreEqual(new MailApplicationFailure(MailFailureKind.UnsupportedFeature, "The requested mail features are not supported."), unsupported.Failure);
        var missing = await service.ValidatePlanAsync(new(authentication, new([MailFeature.Messages], 0))).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, missing.Failure?.Kind);
        var negative = await service.ValidatePlanAsync(new(authentication, new([MailFeature.Basic], -1))).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, negative.Failure?.Kind);
        var excessive = await service.ValidatePlanAsync(new(authentication,
            new([MailFeature.Basic], fixture.Configuration.Jmap.MaxCallsInRequest + 1))).ConfigureAwait(false);
        Assert.AreEqual(MailResourceLimit.OperationCount, excessive.Failure?.Limit);
        Assert.IsNotNull(excessive.Profile);
        // The profile carries domain policy for Gateway admission; the failure
        // itself must not carry a rendered problem or wire-limit identifier.
        var serialized = JsonSerializer.Serialize(excessive.Failure, ContractOptions());
        Assert.IsFalse(serialized.Contains("urn:ietf:", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("maxCallsInRequest", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("\"title\"", StringComparison.Ordinal));
        var accepted = await service.ValidatePlanAsync(new(authentication, new([MailFeature.Basic], 0))).ConfigureAwait(false);
        Assert.AreEqual(JmapApplicationOutcomes.Ok, accepted.Outcome, StringComparer.Ordinal);
        Assert.IsNull(accepted.Failure);
    }

    [TestMethod]
    public async Task AuthenticationStillPrecedesFeatureAndCapacityFailures()
    {
        var fixture = (await CreateAuthenticatedFixtureAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IJmapApplicationService>();
        var limiter = scope.ServiceProvider.GetRequiredService<JmapConcurrencyLimiter>();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < fixture.Configuration.Jmap.MaxConcurrentRequests; index++)
                leases.Add(await limiter.AcquireRequestAsync(CancellationToken.None).ConfigureAwait(false));
            var invalid = await service.ValidatePlanAsync(new(Authentication(fixture, "bad"),
                new([MailFeature.Unsupported], -1))).ConfigureAwait(false);
            Assert.AreEqual(JmapApplicationOutcomes.Unauthorized, invalid.Outcome, StringComparer.Ordinal);
            Assert.IsNull(invalid.Failure);
            var valid = await service.ValidatePlanAsync(new(Authentication(fixture, "good"),
                new([MailFeature.Unsupported], -1))).ConfigureAwait(false);
            Assert.AreEqual(MailResourceLimit.RequestConcurrency, valid.Failure?.Limit);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [TestMethod]
    public async Task WorkerUploadAndChangeFailuresUseDomainValues()
    {
        var fixture = (await CreateAuthenticatedFixtureAsync(1).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IJmapApplicationService>();
        var authentication = Authentication(fixture, "good");
        var oversized = await service.UploadAsync(new(authentication, fixture.AccountId, "text/plain", [1, 2])).ConfigureAwait(false);
        Assert.AreEqual(MailResourceLimit.UploadSize, oversized.Failure?.Limit);
        var limiter = scope.ServiceProvider.GetRequiredService<JmapConcurrencyLimiter>();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < fixture.Configuration.Jmap.MaxConcurrentUploads; index++)
                leases.Add(await limiter.AcquireUploadAsync(CancellationToken.None).ConfigureAwait(false));
            var busy = await service.UploadAsync(new(authentication, fixture.AccountId, "text/plain", [1])).ConfigureAwait(false);
            Assert.AreEqual(MailResourceLimit.UploadConcurrency, busy.Failure?.Limit);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
        var selection = await service.PollChangesAsync(new(authentication, null, ["Unknown"])).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.InvalidSelection, selection.Failure?.Kind);
    }

    [TestMethod]
    [DataRow("URN:IETF:PARAMS:JMAP:CORE", "unknownCapability")]
    [DataRow("urn:example:unsupported", "unknownCapability")]
    [DataRow("urn:ietf:params:jmap:mail", "notRequest")]
    public async Task RealHttpRouteRendersFeatureFailures(string capability, string type)
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var response = await fixture.SendAsync("POST", "/jmap/api",
            new JsonObject { ["using"] = new JsonArray(capability), ["methodCalls"] = new JsonArray() }.ToJsonString(), "application/json").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
        Assert.AreEqual("urn:ietf:params:jmap:error:" + type, problem["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(400, problem["status"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task RealHttpRouteKeepsMissingMethodFeatureAtInvocationScope()
    {
        var fixture = (await DavFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var response = await fixture.SendAsync("POST", "/jmap/api", """
            {"using":["urn:ietf:params:jmap:core","urn:ietf:params:jmap:core"],"methodCalls":[
              ["Mailbox/get",{},"missing"],["Core/echo",{"value":"after"},"next"]]}
            """, "application/json").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
        Assert.AreEqual("unknownMethod", body["methodResponses"]![0]![1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Core/echo", body["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("after", body["methodResponses"]![1]![1]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private static JsonSerializerOptions ContractOptions() => new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    private static Task<JmapFixture> CreateAuthenticatedFixtureAsync(long? maximumUnreferencedBlobBytes = null) => JmapFixture.CreateAsync(maximumUnreferencedBlobBytes, configureServices: services =>
        services.AddScoped<IMailAuthenticator, TestAuthenticator>());

    private static ProtocolAuthentication Authentication(JmapFixture fixture, string password) =>
        new(ProtocolAuthenticationKinds.Password, fixture.User.Username, password);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "This fixture implementation is activated through the test service provider's registered generic interface mapping.")]
    private sealed class TestAuthenticator(EmailDbContext database) : IMailAuthenticator
    {
        public async Task<AuthenticatedMailUser?> AuthenticateAsync(string username, string password,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(password, "good", StringComparison.Ordinal))
                return null;
            var user = await database.Users.SingleOrDefaultAsync(user => user.Username == username, cancellationToken).ConfigureAwait(false);
            return user is null ? null : new AuthenticatedMailUser(user.Id, user.Username);
        }
    }

    private sealed class ProbeMethod(MailFeature feature, Action onInvoke) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => feature;
        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken)
        {
            onInvoke();
            return Task.FromResult(new JmapMethodResponse(Operation, arguments));
        }
    }
}
