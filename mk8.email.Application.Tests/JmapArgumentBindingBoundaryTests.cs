using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapArgumentBindingBoundaryTests
{
    [TestMethod]
    public async Task GatewayTranslatesReferencesBeforeTypedTransportAndWorkerResolution()
    {
        var document = JsonNode.Parse("""
            {"using":["urn:ietf:params:jmap:core"],"methodCalls":[
              ["Core/echo",{"a/b":{"~ids":["zero",null,"two"]},"x":1,"X":2},"source"],
              ["Core/echo",{"ordinary":true,"#copied":{"resultOf":"source","name":"Core/echo","path":"/a~1b/~0ids/2"}},"target"]]}
            """);
        var batch = GatewayJmapBatchCodec.Parse(document, out _);
        var input = batch.Invocations[1];
        Assert.IsFalse(input.Arguments.ContainsKey("#copied"));
        Assert.IsFalse(input.Arguments.ContainsKey("copied"));
        Assert.IsTrue(input.Arguments["ordinary"]!.GetValue<bool>());
        var binding = input.Bindings!.Single();
        Assert.AreEqual("copied", binding.Target);
        Assert.AreEqual("source", binding.SourceCorrelationId);
        Assert.AreEqual(MailOperationKind.Echo, binding.SourceOperation);
        CollectionAssert.AreEqual(new[] { "a/b", "~ids", "2" }, binding.Path.Select(part => part.Property).ToArray());
        Assert.AreEqual(2, binding.Path[2].ArrayIndex);
        Assert.AreEqual(ApplicationBindingFailure.None, binding.Failure);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
        var transported = JsonSerializer.Deserialize<JmapApplicationBatch>(JsonSerializer.Serialize(batch, options), options)!;
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var result = await JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), transported, fixture.User);
        Assert.AreEqual("two", result.Invocations[1].Arguments["copied"]!.GetValue<string>());
        Assert.AreEqual(1, result.Invocations[0].Arguments["x"]!.GetValue<int>());
        Assert.AreEqual(2, result.Invocations[0].Arguments["X"]!.GetValue<int>());
        Assert.IsFalse(result.Invocations[1].Arguments.ContainsKey("#copied"));
    }

    [TestMethod]
    [DataRow("{\"value\":null}", "", "{\"value\":null}", null)]
    [DataRow("{\"\":\"empty\"}", "/", "\"empty\"", null)]
    [DataRow("{\"\":{\"child\":true}}", "//child", "true", null)]
    [DataRow("{\"a/b\":{\"~key\":1}}", "/a~1b/~0key", "1", null)]
    [DataRow("{\"~1\":2}", "/~01", "2", null)]
    [DataRow("{\"array\":[\"zero\",\"one\"]}", "/array/0", "\"zero\"", null)]
    [DataRow("{\"array\":[\"zero\",\"one\"]}", "/array/1", "\"one\"", null)]
    [DataRow("{\"array\":[\"zero\",\"one\"]}", "/array/01", null, "invalidResultReference")]
    [DataRow("{\"array\":[0,1]}", "/array/-1", null, "invalidResultReference")]
    [DataRow("{\"array\":[0,1]}", "/array/+1", null, "invalidResultReference")]
    [DataRow("{\"array\":[0,1]}", "/array/ 1", null, "invalidResultReference")]
    [DataRow("{\"array\":[0,1]}", "/array/2147483648", null, "invalidResultReference")]
    [DataRow("{\"array\":[0,1]}", "/array/2", null, "invalidResultReference")]
    [DataRow("{\"array\":[[1,null],[2],[]]}", "/array/*", "[1,null,2]", null)]
    [DataRow("{\"groups\":[{\"ids\":[1,2]},{\"ids\":[3]}]}", "/groups/*/ids", "[1,2,3]", null)]
    [DataRow("{\"groups\":[{\"ids\":[1,2]},{\"ids\":[3]}]}", "/groups/*/ids/*", "[1,2,3]", null)]
    [DataRow("{\"empty\":[]}", "/empty/*/missing", "[]", null)]
    [DataRow("{\"array\":[{\"child\":1},2]}", "/array/*/child", null, "invalidResultReference")]
    [DataRow("{\"*\":{\"id\":\"literal\"}}", "/*/id", "\"literal\"", null)]
    [DataRow("{\"01\":\"property\"}", "/01", "\"property\"", null)]
    [DataRow("{\"value\":null}", "/value", "null", null)]
    [DataRow("{\"value\":null}", "/value/missing", null, "invalidResultReference")]
    [DataRow("{}", "/missing", null, "invalidResultReference")]
    [DataRow("{}", "not/a/pointer", null, "invalidResultReference")]
    [DataRow("{}", "/a~", null, "invalidResultReference")]
    [DataRow("{}", "/a~2", null, "invalidResultReference")]
    public async Task DecodedSelectorsPreservePointerBehavior(string source, string path, string? expected, string? error)
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var result = await fixture.InvokeAsync(Request(JsonNode.Parse(source)!.AsObject(), new JsonObject
        {
            ["#value"] = Reference("source", path),
        }));
        var response = result["methodResponses"]![1]!;
        if (error is not null)
        {
            Assert.AreEqual("error", response[0]!.GetValue<string>());
            Assert.AreEqual(error, response[1]!["type"]!.GetValue<string>());
        }
        else
        {
            Assert.AreEqual("Core/echo", response[0]!.GetValue<string>());
            Assert.IsTrue(response[1]!.AsObject().ContainsKey("value"));
            Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected!), response[1]!["value"]));
        }
    }

    [TestMethod]
    [DataRow("{\"#value\":{\"resultOf\":\"missing\",\"name\":\"Core/echo\",\"path\":\"\"},\"#\":{}}", "invalidResultReference")]
    [DataRow("{\"#\":{},\"#value\":{\"resultOf\":\"missing\",\"name\":\"Core/echo\",\"path\":\"\"}}", "invalidArguments")]
    [DataRow("{\"#value\":{\"resultOf\":\"source\",\"name\":\"Core/echo\",\"path\":\"/missing\"},\"value\":0}", "invalidArguments")]
    [DataRow("{\"#value\":null}", "invalidResultReference")]
    [DataRow("{\"#value\":{\"resultOf\":\"source\",\"name\":\"Core/echo\",\"path\":0}}", "invalidResultReference")]
    [DataRow("{\"#value\":{\"resultOf\":\"source\",\"name\":\"Core/echo\",\"path\":\"\",\"unknown\":true}}", "invalidResultReference")]
    [DataRow("{\"#value\":{\"resultOf\":\"source\",\"name\":\"wrong\",\"path\":\"\"}}", "invalidResultReference")]
    [DataRow("{\"#value\":{\"resultOf\":\"target\",\"name\":\"Core/echo\",\"path\":\"\"}}", "invalidResultReference")]
    public async Task DeferredFailuresPreserveOriginalBindingOrder(string input, string expected)
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var result = await fixture.InvokeAsync(Request(new JsonObject(), JsonNode.Parse(input)!.AsObject()));
        Assert.AreEqual(expected, result["methodResponses"]![1]![1]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ATargetBeginningWithHashIsNotReinterpretedAsAnotherReference()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var result = await fixture.InvokeAsync(Request(new JsonObject { ["value"] = "copied" }, new JsonObject
        {
            ["##target"] = Reference("source", "/value"),
        }));
        Assert.AreEqual("copied", result["methodResponses"]![1]![1]!["#target"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task InvalidReferenceIsAnInvocationErrorAndDoesNotAbortOtherCalls()
    {
        var calls = 0;
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(() => calls++)));
        var document = Request(new JsonObject(), new JsonObject { ["#value"] = Reference("source", "/bad~2") });
        var invocations = document["methodCalls"]!.AsArray();
        invocations[0]![0] = "Mailbox/query";
        invocations.Add(new JsonArray("Mailbox/query", new JsonObject(), "last"));
        var result = await fixture.InvokeAsync(document);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(3, result["methodResponses"]!.AsArray().Count);
        Assert.AreEqual("invalidResultReference", result["methodResponses"]![1]![1]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DependencyValuesAreClonedWithoutMutatingCompletedResults()
    {
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new MutatingMethod()));
        var document = Request(new JsonObject { ["object"] = new JsonObject { ["name"] = "original" } },
            new JsonObject { ["#copy"] = Reference("source", "/object") });
        document["methodCalls"]![1]![0] = "Mailbox/set";
        document["methodCalls"]!.AsArray().Add(new JsonArray("Core/echo",
            new JsonObject { ["#copy"] = Reference("source", "/object") }, "last"));
        var result = await fixture.InvokeAsync(document);
        Assert.AreEqual("original", result["methodResponses"]![0]![1]!["object"]!["name"]!.GetValue<string>());
        Assert.AreEqual("changed", result["methodResponses"]![1]![1]!["copy"]!["name"]!.GetValue<string>());
        Assert.AreEqual("original", result["methodResponses"]![2]![1]!["copy"]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"target\":\"value\",\"sourceCorrelationId\":\"source\",\"sourceOperation\":2,\"path\":null}")]
    [DataRow("{\"target\":\"value\",\"sourceCorrelationId\":\"source\",\"sourceOperation\":2,\"path\":[null]}")]
    [DataRow("{\"target\":\"value\",\"sourceCorrelationId\":\"source\",\"sourceOperation\":2,\"path\":[{\"property\":\"items\",\"arrayIndex\":-1}]}")]
    [DataRow("{\"target\":\"value\",\"sourceCorrelationId\":\"source\",\"sourceOperation\":2,\"path\":[{\"property\":\"items\",\"arrayIndex\":0,\"allArrayItems\":true}]}")]
    [DataRow("{\"target\":\"value\",\"sourceCorrelationId\":\"source\",\"sourceOperation\":2,\"path\":[],\"failure\":999}")]
    public async Task WorkerRejectsIncompleteTypedDependenciesBeforeEarlierCalls(string bindingJson)
    {
        var calls = 0;
        await using var fixture = await JmapFixture.CreateAsync(configureServices: services =>
            JmapFixture.OverrideMethod(services, new ProbeMethod(() => calls++)));
        using var scope = fixture.Services.CreateScope();
        var binding = JsonSerializer.Deserialize<ApplicationArgumentBinding>(bindingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.FindFolders, new JsonObject(), "first"),
             new JmapApplicationCall(MailOperationKind.Echo, new JsonObject(), "second", [binding!])]);
        var exception = await Assert.ThrowsAsync<GatewayJmapBatchCodec.RequestException>(() =>
            JmapFixture.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User));
        Assert.AreEqual("urn:ietf:params:jmap:error:notRequest", exception.Problem.Type);
        Assert.AreEqual(0, calls);
    }

    private static JsonObject Request(JsonObject source, JsonObject target) => new()
    {
        ["using"] = new JsonArray(GatewayJmapFeatureCodec.CoreCapability),
        ["methodCalls"] = new JsonArray(
            new JsonArray("Core/echo", source, "source"), new JsonArray("Core/echo", target, "target")),
    };

    private static JsonObject Reference(string source, string path) => new()
    {
        ["resultOf"] = source,
        ["name"] = "Core/echo",
        ["path"] = path,
    };

    private sealed class ProbeMethod(Action onInvoke) : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.FindFolders;
        public MailFeature Feature => MailFeature.Basic;
        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken = default)
        {
            onInvoke();
            return Task.FromResult(new JmapMethodResponse(Operation, arguments));
        }
    }

    private sealed class MutatingMethod : IJmapMethod
    {
        public MailOperationKind Operation => MailOperationKind.MutateFolders;
        public MailFeature Feature => MailFeature.Basic;
        public Task<JmapMethodResponse> InvokeAsync(JmapInvocationContext context, JsonObject arguments,
            CancellationToken cancellationToken = default)
        {
            arguments["copy"]!["name"] = "changed";
            return Task.FromResult(new JmapMethodResponse(Operation, arguments));
        }
    }
}
