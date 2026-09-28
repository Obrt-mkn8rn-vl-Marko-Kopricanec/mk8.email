using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapCreationReferenceBoundaryTests
{
    [TestMethod]
    public void GatewayMarksNestedValuesAndKeysWithoutRewritingOpaqueArguments()
    {
        var arguments = JsonNode.Parse("""
            {"#key":{"id":"#new","items":["#new","#bad/id",null]},
             "x":"literal","X":"#other","opaque":"#not-an-id"}
            """)!.AsObject();
        var unchanged = arguments.DeepClone();

        var aliases = GatewayJmapReferenceAliasCodec.Collect(arguments);

        Assert.AreEqual("key", aliases["#key"]);
        Assert.AreEqual("new", aliases["#new"]);
        Assert.AreEqual("bad/id", aliases["#bad/id"]);
        Assert.AreEqual("other", aliases["#other"]);
        Assert.AreEqual("not-an-id", aliases["#not-an-id"]);
        Assert.HasCount(5, aliases);
        Assert.IsTrue(JsonNode.DeepEquals(unchanged, arguments));
        Assert.AreEqual("literal", arguments["x"]!.GetValue<string>());
        Assert.AreEqual("#other", arguments["X"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task WorkerUsesOnlyTypedAliasesAndCanResolveAnEntityCreatedInsideTheOperation()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = GatewayJmapReferenceAliasCodec.Collect(new JsonObject { ["parentId"] = "#parent" });
        var context = new JmapInvocationContext(fixture.User, new HashSet<MailFeature> { MailFeature.Basic }, known)
        {
            ReferenceAliases = new Dictionary<string, string>(aliases, StringComparer.Ordinal),
        };

        Assert.IsNull(context.ResolveId("#parent"));
        Assert.AreEqual("#unmarked", context.ResolveId("#unmarked"));
        Assert.IsTrue(context.TryGetReferenceKey("#parent", out var creationKey));
        Assert.AreEqual("parent", creationKey);
        known["parent"] = fixture.InboxMailboxId;
        Assert.AreEqual(fixture.InboxMailboxId, context.ResolveId("#parent"));
        Assert.IsFalse(context.TryGetReferenceKey("#unmarked", out _));
    }

    [TestMethod]
    public async Task ResolvedResultReferenceCanSupplyAForwardCreationReferenceToTheSameWorkerOperation()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var request = JsonNode.Parse("""
            {"using":["urn:ietf:params:jmap:core","urn:ietf:params:jmap:mail"],
             "methodCalls":[
               ["Core/echo",{"create":{"child":{"name":"Child","parentId":"#parent"},
                                       "parent":{"name":"Parent"}}},"seed"],
               ["Mailbox/set",{"accountId":"pending-account",
                               "#create":{"resultOf":"seed","name":"Core/echo","path":"/create"}},"mutate"]]}
            """)!;
        request["methodCalls"]![1]![1]!["accountId"] = fixture.AccountId;
        var response = await fixture.InvokeAsync(request);

        var result = response["methodResponses"]![1]!;
        Assert.AreEqual("Mailbox/set", result[0]!.GetValue<string>());
        Assert.IsNotNull(result[1]!["created"]!["parent"]!["id"]);
        Assert.IsNotNull(result[1]!["created"]!["child"]!["id"]);
        Assert.IsNull(result[1]!["notCreated"]);
    }

    [TestMethod]
    public async Task ExcessiveResolvedDepthIsAnInvocationErrorNotAnUnhandledGatewayException()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        JsonNode node = JsonValue.Create("#parent")!;
        for (var index = 0; index < 65; index++)
            node = new JsonObject { ["nested"] = node };
        var arguments = new JsonObject { ["tree"] = node };
        var batch = new JmapApplicationBatch([MailFeature.Basic],
            [new JmapApplicationCall(MailOperationKind.ReadFolders, arguments, "deep")]);
        using var scope = fixture.Services.CreateScope();
        var result = await JmapFixture.ProcessBatchAsync(
            scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>(), batch, fixture.User);

        Assert.AreEqual(MailOperationKind.Failure, result.Invocations[0].Operation);
        Assert.AreEqual("invalidArguments", result.Invocations[0].Arguments["type"]!.GetValue<string>());
    }
}
