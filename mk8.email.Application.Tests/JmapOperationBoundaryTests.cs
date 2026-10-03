using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapOperationBoundaryTests
{
    [TestMethod]
    [DataRow(MailOperationFailureReason.NotSupported, "unknownMethod")]
    [DataRow(MailOperationFailureReason.InternalFailure, "serverFail")]
    [DataRow(MailOperationFailureReason.PartiallyCompleted, "serverPartialFail")]
    public void GatewayRendersNeutralOperationFailures(MailOperationFailureReason reason, string expected)
    {
        var response = JmapMethodResponse.Failure(reason, "domain explanation");
        Assert.IsFalse(response.Arguments.ContainsKey("type"));
        Assert.IsFalse(response.Arguments.ContainsKey("description"));
        var decoded = GatewayMailOperationFailureCodec.Decode(response.Arguments);
        Assert.AreEqual(reason, decoded.Reason);
        var rendered = GatewayMailOperationFailureCodec.Render(decoded);
        Assert.AreEqual(expected, rendered["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("domain explanation", rendered["description"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(MailOperationFailureReason.None)]
    [DataRow((MailOperationFailureReason)999)]
    public void InvalidNeutralFailureReasonsAreRejected(MailOperationFailureReason reason)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => JmapMethodResponse.Failure(reason));
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayMailOperationFailureCodec.Decode(
            new JsonObject { ["reason"] = (int)reason }));
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayMailOperationFailureCodec.Render(new(reason)));
    }

    [TestMethod]
    public void LegacyWireErrorsCannotMasqueradeAsNeutralFailures()
    {
        Assert.ThrowsExactly<JsonException>(() => GatewayMailOperationFailureCodec.Decode(
            new JsonObject { ["type"] = "serverPartialFail" }));
        Assert.ThrowsExactly<JsonException>(() => GatewayMailOperationFailureCodec.Decode(
            new JsonObject { ["reason"] = (int)MailOperationFailureReason.InternalFailure, ["type"] = "serverFail" }));
    }

    [TestMethod]
    public async Task WorkerUnsupportedOperationReturnsOnlyDomainFailureData()
    {
        var fixture = await JmapFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var result = await processor.ExecuteAsync(new MailOperationCommand(
            [MailFeature.Basic], MailOperationKind.None, new JsonObject(),
            new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal)),
            fixture.User, Guid.CreateVersion7()).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.Failure, result.Response.Operation);
        var data = (JsonObject)ApplicationValueCodec.Decode(result.Response.Data)!;
        Assert.AreEqual(MailOperationFailureReason.NotSupported, GatewayMailOperationFailureCodec.Decode(data).Reason);
        Assert.IsFalse(data.ContainsKey("type"));
        Assert.IsFalse(data.ContainsKey("description"));
    }

    [TestMethod]
    [DataRow("Core/echo", MailOperationKind.Echo, 2)]
    [DataRow("Mailbox/get", MailOperationKind.ReadFolders, 10)]
    [DataRow("Mailbox/query", MailOperationKind.FindFolders, 11)]
    [DataRow("Mailbox/changes", MailOperationKind.ReadFolderChanges, 12)]
    [DataRow("Mailbox/queryChanges", MailOperationKind.FindFolderChanges, 13)]
    [DataRow("Mailbox/set", MailOperationKind.MutateFolders, 14)]
    [DataRow("Thread/get", MailOperationKind.ReadThreads, 20)]
    [DataRow("Thread/changes", MailOperationKind.ReadThreadChanges, 21)]
    [DataRow("Email/get", MailOperationKind.ReadMessages, 30)]
    [DataRow("Email/query", MailOperationKind.FindMessages, 31)]
    [DataRow("Email/changes", MailOperationKind.ReadMessageChanges, 32)]
    [DataRow("Email/queryChanges", MailOperationKind.FindMessageChanges, 33)]
    [DataRow("Email/set", MailOperationKind.MutateMessages, 34)]
    [DataRow("Email/import", MailOperationKind.ImportMessages, 35)]
    [DataRow("Email/copy", MailOperationKind.CopyMessages, 36)]
    [DataRow("Email/parse", MailOperationKind.ParseMessages, 37)]
    [DataRow("SearchSnippet/get", MailOperationKind.ReadSearchSnippets, 38)]
    [DataRow("Identity/get", MailOperationKind.ReadSenderIdentities, 40)]
    [DataRow("Identity/changes", MailOperationKind.ReadSenderIdentityChanges, 41)]
    [DataRow("Identity/set", MailOperationKind.MutateSenderIdentities, 42)]
    [DataRow("EmailSubmission/get", MailOperationKind.ReadSubmissions, 50)]
    [DataRow("EmailSubmission/query", MailOperationKind.FindSubmissions, 51)]
    [DataRow("EmailSubmission/changes", MailOperationKind.ReadSubmissionChanges, 52)]
    [DataRow("EmailSubmission/queryChanges", MailOperationKind.FindSubmissionChanges, 53)]
    [DataRow("EmailSubmission/set", MailOperationKind.MutateSubmissions, 54)]
    [DataRow("VacationResponse/get", MailOperationKind.ReadVacationSettings, 60)]
    [DataRow("VacationResponse/set", MailOperationKind.MutateVacationSettings, 61)]
    [DataRow("PushSubscription/get", MailOperationKind.ReadNotificationSubscriptions, 70)]
    [DataRow("PushSubscription/set", MailOperationKind.MutateNotificationSubscriptions, 71)]
    [DataRow("Blob/copy", MailOperationKind.CopyBinaryObjects, 80)]
    [DataRow("AddressBook/get", MailOperationKind.ReadAddressBooks, 90)]
    [DataRow("AddressBook/changes", MailOperationKind.ReadAddressBookChanges, 91)]
    [DataRow("AddressBook/set", MailOperationKind.MutateAddressBooks, 92)]
    [DataRow("ContactCard/get", MailOperationKind.ReadContacts, 100)]
    [DataRow("ContactCard/query", MailOperationKind.FindContacts, 101)]
    [DataRow("ContactCard/changes", MailOperationKind.ReadContactChanges, 102)]
    [DataRow("ContactCard/queryChanges", MailOperationKind.FindContactChanges, 103)]
    [DataRow("ContactCard/set", MailOperationKind.MutateContacts, 104)]
    [DataRow("ContactCard/copy", MailOperationKind.CopyContacts, 105)]
    public void GatewayOwnsEveryMethodNameAndStableOperationNumber(string name, MailOperationKind operation, int number)
    {
        Assert.AreEqual(number, (int)operation);
        Assert.AreEqual(operation, GatewayJmapOperationCodec.DecodeCall(name));
        Assert.AreEqual(operation, GatewayJmapOperationCodec.DecodeReference(name));
        Assert.AreEqual(name, GatewayJmapOperationCodec.Render(operation), StringComparer.Ordinal);
        Assert.AreEqual(MailOperationKind.None, GatewayJmapOperationCodec.DecodeCall(name.ToUpperInvariant()));
    }

    [TestMethod]
    public async Task EveryRegisteredHandlerHasExactlyOneGatewayMapping()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var methods = scope.ServiceProvider.GetServices<IJmapMethod>().ToArray();
        Assert.IsEmpty(methods);
        foreach (var method in methods)
            Assert.AreEqual(method.Operation, GatewayJmapOperationCodec.DecodeCall(GatewayJmapOperationCodec.Render(method.Operation)));
        CollectionAssert.AreEquivalent(Enum.GetValues<MailOperationKind>().Except([MailOperationKind.None, MailOperationKind.Failure, MailOperationKind.Echo]).ToArray(),
            methods.Select(method => method.Operation)
                .Concat([MailOperationKind.ReadFolders, MailOperationKind.MutateFolders,
                    MailOperationKind.ReadThreads,
                    MailOperationKind.ReadFolderChanges,
                    MailOperationKind.ReadThreadChanges, MailOperationKind.ReadMessageChanges,
                    MailOperationKind.ReadMessages, MailOperationKind.ParseMessages,
                    MailOperationKind.MutateMessages,
                    MailOperationKind.ReadSenderIdentityChanges, MailOperationKind.ReadSubmissionChanges,
                    MailOperationKind.ReadAddressBooks, MailOperationKind.ReadAddressBookChanges,
                    MailOperationKind.MutateAddressBooks,
                    MailOperationKind.ReadSenderIdentities,
                    MailOperationKind.MutateSenderIdentities,
                    MailOperationKind.ReadSubmissions,
                    MailOperationKind.MutateSubmissions,
                    MailOperationKind.FindSubmissions,
                    MailOperationKind.FindSubmissionChanges,
                    MailOperationKind.FindFolders,
                    MailOperationKind.FindFolderChanges,
                    MailOperationKind.FindMessages,
                    MailOperationKind.FindMessageChanges,
                    MailOperationKind.ReadSearchSnippets,
                    MailOperationKind.CopyContacts,
                    MailOperationKind.FindContacts,
                    MailOperationKind.FindContactChanges,
                    MailOperationKind.ReadContacts,
                    MailOperationKind.MutateContacts,
                    MailOperationKind.ImportMessages,
                    MailOperationKind.CopyMessages,
                    MailOperationKind.ReadVacationSettings,
                    MailOperationKind.MutateVacationSettings,
                    MailOperationKind.ReadNotificationSubscriptions,
                    MailOperationKind.MutateNotificationSubscriptions,
                    MailOperationKind.ReadContactChanges,
                    MailOperationKind.CopyBinaryObjects]).ToArray());
    }

    [TestMethod]
    [DataRow("Unknown/run")]
    [DataRow("core/echo")]
    [DataRow("Core/echo ")]
    [DataRow("error")]
    [DataRow("")]
    public async Task UnsupportedWireNamesStayInvocationErrorsAndDoNotAbortTheBatch(string name)
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(GatewayJmapFeatureCodec.CoreCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(name, new JsonObject(), "unknown"),
                new JsonArray("Core/echo", new JsonObject { ["value"] = name }, "next")),
        }).ConfigureAwait(false);
        Assert.AreEqual("error", response["methodResponses"]![0]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("unknownMethod", response["methodResponses"]![0]![1]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Core/echo", response["methodResponses"]![1]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(name, response["methodResponses"]![1]![1]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ErrorReferencesAreDistinctFromUnknownNamesAfterTransport()
    {
        var batch = GatewayJmapBatchCodec.Parse(JsonNode.Parse("""
            {"using":["urn:ietf:params:jmap:core"],"methodCalls":[
              ["Unknown/run",{},"source"],
              ["Core/echo",{"#value":{"resultOf":"source","name":"error","path":"/type"}},"match"],
              ["Core/echo",{"#value":{"resultOf":"source","name":"Unknown/run","path":"/type"}},"unknown"],
              ["Core/echo",{"#value":{"resultOf":"source","name":"Error","path":"/type"}},"case"]]}
            """), out _);
        Assert.AreEqual(MailOperationKind.None, batch.Invocations[0].Operation);
        Assert.AreEqual(MailOperationKind.Failure, batch.Invocations[1].Bindings![0].SourceOperation);
        Assert.AreEqual(MailOperationKind.None, batch.Invocations[2].Bindings![0].SourceOperation);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
        var encoded = JsonSerializer.SerializeToNode(batch, options)!;
        Assert.IsNull(encoded["invocations"]![0]!["name"]);
        Assert.IsNull(encoded["invocations"]![1]!["bindings"]![0]!["sourceName"]);
        var transported = JsonSerializer.Deserialize<JmapApplicationBatch>(encoded, options)!;
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var result = await JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), transported, fixture.User).ConfigureAwait(false);
        Assert.AreEqual("unknownMethod", result.Invocations[1].Arguments["value"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidResultReference", result.Invocations[2].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("invalidResultReference", result.Invocations[3].Arguments["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task OperationTranslationDoesNotTouchOpaqueBusinessNames()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var response = await fixture.InvokeAsync("""
            {"using":["urn:ietf:params:jmap:core"],"methodCalls":[
              ["Core/echo",{"name":"Unknown/run","Name":"Email/set","sourceName":"error"},"opaque"]]}
            """).ConfigureAwait(false);
        var value = response["methodResponses"]![0]![1]!;
        Assert.AreEqual("Unknown/run", value["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Email/set", value["Name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("error", value["sourceName"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("{\"name\":\"Core/echo\",\"arguments\":{},\"correlationId\":\"one\"}")]
    [DataRow("{\"Operation\":2,\"arguments\":{},\"correlationId\":\"one\"}")]
    public void OldOrWrongCaseCallDiscriminatorsCannotBecomeUnknownOperations(string json)
    {
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<JmapApplicationCall>(json,
            SerializationOptions1));
    }

    [TestMethod]
    public void OldReferenceAndResultDiscriminatorsAreNotSilentlyDefaulted()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<ApplicationArgumentBinding>(
            """{"target":"value","sourceCorrelationId":"one","sourceName":"Core/echo","path":[]}""", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<JmapApplicationInvocation>(
            """{"name":"Core/echo","arguments":{},"correlationId":"one"}""", options));
    }

    [TestMethod]
    [DataRow(MailOperationKind.Failure)]
    [DataRow((MailOperationKind)(-1))]
    [DataRow((MailOperationKind)999)]
    public async Task InvalidTypedCallRejectsTheEntireBatchBeforeEarlierInvocations(MailOperationKind operation)
    {
        var calls = 0;
        var fixture = (await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(MailOperationKind.FindFolders, () => calls++))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new(MailOperationKind.FindFolders, new JsonObject(), "first"), new(operation, new JsonObject(), "invalid")]);
        await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() =>
            JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task UndefinedTypedDependencyRejectsBeforeEarlierInvocations()
    {
        var calls = 0;
        var fixture = (await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(MailOperationKind.FindFolders, () => calls++))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new(MailOperationKind.FindFolders, new JsonObject(), "first"), new(MailOperationKind.Echo, new JsonObject(), "invalid",
                [new("value", "first", (MailOperationKind)999, [])])]);
        await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() =>
            JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User)).ConfigureAwait(false);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(MailOperationKind.None)]
    [DataRow(MailOperationKind.Failure)]
    [DataRow((MailOperationKind)999)]
    [DataRow(MailOperationKind.Echo)]
    public async Task InvalidOrDuplicateHandlerRegistrationFailsClosed(MailOperationKind operation)
    {
        var fixture = (await JmapFixture.CreateAsync(configureServices: services =>
            services.AddSingleton<IJmapMethod>(new ProbeMethod(operation, () => Assert.Fail("No handler should execute.")))).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        Assert.ThrowsExactly<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>());
    }

    [TestMethod]
    [DataRow(MailOperationKind.None)]
    [DataRow((MailOperationKind)999)]
    public void GatewayRefusesUnrenderableResults(MailOperationKind operation)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayJmapOperationCodec.Render(operation));
        Assert.AreEqual("error", GatewayJmapOperationCodec.Render(MailOperationKind.Failure), StringComparer.Ordinal);
    }

    private sealed class ProbeMethod(MailOperationKind operation, Action onInvoke) : IJmapMethod
    {
        public MailOperationKind Operation => operation;
        public MailFeature Feature => MailFeature.Basic;
        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken)
        {
            onInvoke();
            return Task.FromResult(new JmapMethodResponse(Operation, arguments));
        }
    }
    private static readonly JsonSerializerOptions SerializationOptions1 = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
}
