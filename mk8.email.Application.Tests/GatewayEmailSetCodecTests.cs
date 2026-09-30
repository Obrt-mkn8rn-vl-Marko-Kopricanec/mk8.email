using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayEmailSetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string EmailId = "E22222222222222222222222222222";

    [TestMethod]
    public void GatewayParsesMutationsAndRendersTypedOutcomes()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ifInState"] = "s1",
            ["create"] = new JsonObject
            {
                ["created"] = new JsonObject
                {
                    ["mailboxIds"] = new JsonObject { ["M33333333333333333333333333333333"] = true },
                    ["subject"] = "Draft",
                },
            },
            ["update"] = new JsonObject
            {
                ["#created"] = new JsonObject { ["keywords/$seen"] = true },
            },
            ["destroy"] = new JsonArray(EmailId, EmailId),
        };
        Assert.IsFalse(GatewayEmailSetCodec.TryParse(arguments, 3, out _, out var failure));
        Assert.AreEqual("requestTooLarge", failure);
        Assert.IsTrue(GatewayEmailSetCodec.TryParse(arguments, 4, out var call, out failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.HasCount(1, call.Command.Creates);
        Assert.HasCount(1, call.Command.Updates);
        Assert.HasCount(1, call.Command.Destroys);
        var draft = call.Command.Creates[0].Draft;
        Assert.IsNotNull(draft.Mime);
        var subject = draft.Mime.Headers.First(header => Encoding.ASCII.GetString(header.RawField.Span) == "Subject");
        Assert.AreEqual("Draft", Encoding.UTF8.GetString(subject.RawValue.Span).Trim());

        var result = new MailMessageMutationResult(MailMessageMutationStatus.Ok, "s1", "s2",
        [
            new("created", new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "thread-1", 123), null),
        ],
        [
            new("#created", Guid.Parse("44444444-4444-4444-4444-444444444444"), null),
        ],
        [
            new(EmailId, null, new(MailMessageMutationError.NotFound, null, null, null)),
        ]);
        var response = GatewayEmailSetCodec.Render(call, result);
        Assert.AreEqual(MailOperationKind.MutateMessages, response.Operation);
        Assert.AreEqual("s2", response.Data["newState"]!.GetValue<string>());
        var created = response.Data["created"]!["created"]!;
        Assert.AreEqual("E44444444444444444444444444444444", created["id"]!.GetValue<string>());
        Assert.AreEqual("B44444444444444444444444444444444", created["blobId"]!.GetValue<string>());
        Assert.AreEqual("Tthread-1", created["threadId"]!.GetValue<string>());
        Assert.AreEqual(123, created["size"]!.GetValue<int>());
        Assert.IsNull(response.Data["updated"]!["E44444444444444444444444444444444"]);
        Assert.AreEqual("notFound", response.Data["notDestroyed"]![EmailId]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayRejectsMalformedEnvelopeAndDefersDraftErrorsUntilAccountAuthorization()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = "not-an-account",
            ["create"] = new JsonObject { ["created"] = new JsonObject { ["opaque"] = true } },
        };
        Assert.IsTrue(GatewayEmailSetCodec.TryParse(arguments, 2, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        Assert.AreEqual(MailMessageMutationError.InvalidProperties, call.Command.Creates[0].Draft.Failure!.Error);
        var denied = GatewayEmailSetCodec.Render(call,
            new MailMessageMutationResult(MailMessageMutationStatus.AccountNotFound, null, null, [], [], []));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
        arguments["unexpected"] = true;
        Assert.IsFalse(GatewayEmailSetCodec.TryParse(arguments, 2, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
        arguments.Remove("unexpected");
        arguments["create"] = new JsonObject { ["bad#key"] = new JsonObject() };
        Assert.IsFalse(GatewayEmailSetCodec.TryParse(arguments, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure);
        arguments["create"] = new JsonObject { ["created"] = JsonValue.Create("not-an-object") };
        Assert.IsFalse(GatewayEmailSetCodec.TryParse(arguments, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public void GatewayRejectsInconsistentResultsAndRendersBlobFailures()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["create"] = new JsonObject { ["created"] = new JsonObject() },
        };
        Assert.IsTrue(GatewayEmailSetCodec.TryParse(arguments, 2, out var call, out _));
        Assert.IsNotNull(call);
        var invalid = new MailMessageMutationResult(MailMessageMutationStatus.Ok, "s1", "s2",
            [new("wrong", null, new(MailMessageMutationError.InvalidProperties, null, null, null))], [], []);
        Assert.Throws<InvalidOperationException>(() => GatewayEmailSetCodec.Render(call, invalid));
        var blobFailure = invalid with
        {
            Created = [new("created", null,
                new(MailMessageMutationError.BlobNotFound, null, null, ["Umissing"]))],
        };
        var response = GatewayEmailSetCodec.Render(call, blobFailure);
        Assert.AreEqual("blobNotFound", response.Data["notCreated"]!["created"]!["type"]!.GetValue<string>());
        Assert.AreEqual("Umissing", response.Data["notCreated"]!["created"]!["notFound"]![0]!
            .GetValue<string>());
    }

    [TestMethod]
    public void GatewayDraftContainsTypedMimePartsAndBlobReferencesInsteadOfWireValues()
    {
        var arguments = JsonNode.Parse("""
            {"accountId":"A11111111111111111111111111111111","create":{"draft":{
              "mailboxIds":{"#folder":true},"keywords":{"$Draft":true},
              "receivedAt":"2026-01-01T00:00:00Z","subject":"Typed draft",
              "bodyValues":{"text":{"value":"Inline content"}},
              "bodyStructure":{"type":"multipart/mixed","subParts":[
                {"partId":"text","type":"text/plain"},
                {"blobId":"U44444444444444444444444444444444","type":"application/octet-stream",
                 "name":"file.bin","disposition":"attachment"}]}}}}
            """)!.AsObject();
        Assert.IsTrue(GatewayEmailSetCodec.TryParse(arguments, 10, out var call, out _));
        var draft = call!.Command.Creates[0].Draft;
        Assert.IsNull(draft.Failure);
        Assert.AreEqual("#folder", draft.FolderReference);
        CollectionAssert.AreEqual(new[] { "$draft" }, draft.Keywords.ToArray());
        Assert.AreEqual(DateTimeKind.Utc, draft.ReceivedAt!.Value.Kind);
        Assert.IsNotNull(draft.Mime);
        Assert.HasCount(3, draft.Mime.Parts);
        var root = draft.Mime.Parts[draft.Mime.RootPart];
        CollectionAssert.AreEqual(new[] { 0, 1 }, root.Children.ToArray());
        Assert.AreEqual("Inline content", draft.Mime.Parts[0].Text);
        Assert.IsNull(draft.Mime.Parts[1].Text);
        Assert.AreEqual("U44444444444444444444444444444444", draft.Mime.Parts[1].BlobReference);
        var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsFalse(json.Contains("bodyValues", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("bodyStructure", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("header:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WorkerRejectsLegacyDraftTreesAndCyclicTypedMimeBeforeMutation()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var arguments = new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject
            {
                ["created"] = new JsonObject
                { ["mailboxIds"] = new JsonObject { [fixture.InboxMailboxId] = true }, ["subject"] = "Draft" }
            },
        };
        Assert.IsTrue(GatewayEmailSetCodec.TryParse(arguments, 2, out var call, out _));
        var serialized = JsonSerializer.SerializeToNode(call!.Command,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var original = serialized["creates"]![0]!["draft"]!.DeepClone();
        serialized["creates"]![0]!["draft"] = JsonSerializer.SerializeToNode(ApplicationValueCodec.Encode(
            new JsonObject { ["subject"] = "Legacy tree" }));
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.MutateMessages, serialized, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        serialized["creates"]![0]!["draft"] = original;
        var mime = original["mime"]!;
        var root = mime["rootPart"]!.GetValue<int>();
        mime["parts"]![root]!["children"] = new JsonArray(root);
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedMutationBeforeBusinessWork()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.MutateMessages, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["ifInState"] = null,
                ["creates"] = new JsonArray(),
                ["updates"] = new JsonArray(),
                ["destroys"] = new JsonArray(),
                ["extra"] = true,
            }, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        command.Arguments.Remove("extra");
        command.Arguments.Remove("creates");
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
