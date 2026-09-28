using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapOperationBoundaryTests
{
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
        Assert.AreEqual(name, GatewayJmapOperationCodec.Render(operation));
        Assert.AreEqual(MailOperationKind.None, GatewayJmapOperationCodec.DecodeCall(name.ToUpperInvariant()));
    }

    [TestMethod]
    public async Task EveryRegisteredHandlerHasExactlyOneGatewayMapping()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var methods = scope.ServiceProvider.GetServices<IJmapMethod>().ToArray();
        Assert.HasCount(37, methods);
        Assert.HasCount(37, methods.Select(method => method.Operation).Distinct().ToArray());
        foreach (var method in methods)
            Assert.AreEqual(method.Operation, GatewayJmapOperationCodec.DecodeCall(GatewayJmapOperationCodec.Render(method.Operation)));
        CollectionAssert.AreEquivalent(Enum.GetValues<MailOperationKind>().Except([MailOperationKind.None, MailOperationKind.Failure, MailOperationKind.Echo]).ToArray(),
            methods.Select(method => method.Operation).Append(MailOperationKind.ReadFolders).ToArray());
    }

    [TestMethod]
    [DataRow("Unknown/run")]
    [DataRow("core/echo")]
    [DataRow("Core/echo ")]
    [DataRow("error")]
    [DataRow("")]
    public async Task UnsupportedWireNamesStayInvocationErrorsAndDoNotAbortTheBatch(string name)
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync(new JsonObject
        {
            ["using"] = new JsonArray(GatewayJmapFeatureCodec.CoreCapability),
            ["methodCalls"] = new JsonArray(new JsonArray(name, new JsonObject(), "unknown"),
                new JsonArray("Core/echo", new JsonObject { ["value"] = name }, "next")),
        });
        Assert.AreEqual("error", response["methodResponses"]![0]![0]!.GetValue<string>());
        Assert.AreEqual("unknownMethod", response["methodResponses"]![0]![1]!["type"]!.GetValue<string>());
        Assert.AreEqual("Core/echo", response["methodResponses"]![1]![0]!.GetValue<string>());
        Assert.AreEqual(name, response["methodResponses"]![1]![1]!["value"]!.GetValue<string>());
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
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var result = await JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), transported, fixture.User);
        Assert.AreEqual("unknownMethod", result.Invocations[1].Arguments["value"]!.GetValue<string>());
        Assert.AreEqual("invalidResultReference", result.Invocations[2].Arguments["type"]!.GetValue<string>());
        Assert.AreEqual("invalidResultReference", result.Invocations[3].Arguments["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task OperationTranslationDoesNotTouchOpaqueBusinessNames()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var response = await fixture.InvokeAsync("""
            {"using":["urn:ietf:params:jmap:core"],"methodCalls":[
              ["Core/echo",{"name":"Unknown/run","Name":"Email/set","sourceName":"error"},"opaque"]]}
            """);
        var value = response["methodResponses"]![0]![1]!;
        Assert.AreEqual("Unknown/run", value["name"]!.GetValue<string>());
        Assert.AreEqual("Email/set", value["Name"]!.GetValue<string>());
        Assert.AreEqual("error", value["sourceName"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("{\"name\":\"Core/echo\",\"arguments\":{},\"correlationId\":\"one\"}")]
    [DataRow("{\"Operation\":2,\"arguments\":{},\"correlationId\":\"one\"}")]
    public void OldOrWrongCaseCallDiscriminatorsCannotBecomeUnknownOperations(string json)
    {
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<JmapApplicationCall>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false }));
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
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(MailOperationKind.FindFolders, () => calls++)));
        using var scope = fixture.Services.CreateScope();
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new(MailOperationKind.FindFolders, new JsonObject(), "first"), new(operation, new JsonObject(), "invalid")]);
        await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() =>
            JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task UndefinedTypedDependencyRejectsBeforeEarlierInvocations()
    {
        var calls = 0;
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(MailOperationKind.FindFolders, () => calls++)));
        using var scope = fixture.Services.CreateScope();
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new(MailOperationKind.FindFolders, new JsonObject(), "first"), new(MailOperationKind.Echo, new JsonObject(), "invalid",
                [new("value", "first", (MailOperationKind)999, [])])]);
        await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() =>
            JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(MailOperationKind.None)]
    [DataRow(MailOperationKind.Failure)]
    [DataRow((MailOperationKind)999)]
    [DataRow(MailOperationKind.Echo)]
    public async Task InvalidOrDuplicateHandlerRegistrationFailsClosed(MailOperationKind operation)
    {
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            services.AddSingleton<IJmapMethod>(new ProbeMethod(operation, () => Assert.Fail("No handler should execute."))));
        using var scope = fixture.Services.CreateScope();
        Assert.ThrowsExactly<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>());
    }

    [TestMethod]
    [DataRow(MailOperationKind.None)]
    [DataRow((MailOperationKind)999)]
    public void GatewayRefusesUnrenderableResults(MailOperationKind operation)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayJmapOperationCodec.Render(operation));
        Assert.AreEqual("error", GatewayJmapOperationCodec.Render(MailOperationKind.Failure));
    }

    private sealed class ProbeMethod(MailOperationKind operation, Action onInvoke) : IJmapMethod
    {
        public MailOperationKind Operation => operation;
        public MailFeature Feature => MailFeature.Basic;
        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken = default)
        {
            onInvoke();
            return Task.FromResult(new JmapMethodResponse(Operation, arguments));
        }
    }
}
