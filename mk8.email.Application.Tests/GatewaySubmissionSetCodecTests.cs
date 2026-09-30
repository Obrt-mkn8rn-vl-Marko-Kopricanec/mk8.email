using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewaySubmissionSetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string ExistingId = "S22222222222222222222222222222222";

    [TestMethod]
    public void GatewayOwnsSubmissionSetSyntaxAndResponseProjection()
    {
        var envelope = new JsonObject
        {
            ["mailFrom"] = new JsonObject { ["email"] = "sender@example.test", ["parameters"] = null },
            ["rcptTo"] = new JsonArray(new JsonObject { ["email"] = "to@example.test", ["parameters"] = null }),
        };
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ifInState"] = "s1",
            ["create"] = new JsonObject
            {
                ["new"] = new JsonObject
                {
                    ["identityId"] = "I33333333333333333333333333333333",
                    ["emailId"] = "E44444444444444444444444444444444",
                    ["envelope"] = envelope.DeepClone(),
                },
            },
            ["update"] = new JsonObject { ["#new"] = new JsonObject { ["undoStatus"] = "final" } },
            ["destroy"] = new JsonArray(ExistingId, ExistingId),
            ["onSuccessUpdateEmail"] = new JsonObject
            {
                ["#new"] = new JsonObject { ["keywords/$seen"] = true },
            },
            ["onSuccessDestroyEmail"] = new JsonArray(ExistingId),
        };
        Assert.IsFalse(GatewaySubmissionSetCodec.TryParse(arguments, 3, out _, out var failure));
        Assert.AreEqual("requestTooLarge", failure);
        Assert.IsTrue(GatewaySubmissionSetCodec.TryParse(arguments, 4, out var call, out failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.HasCount(1, call.Command.Creates);
        Assert.HasCount(1, call.Command.Destroys);
        Assert.HasCount(1, call.Command.OnSuccessUpdates);
        Assert.AreEqual("sender@example.test", call.Command.Creates[0].Draft.Envelope!.Sender.Address);

        var createdId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var created = new MailSubmissionSnapshot(createdId,
            "I33333333333333333333333333333333", "E44444444444444444444444444444444",
            "T44444444444444444444444444444444", new(new("sender@example.test", null), [new("to@example.test", null)]),
            "sender@example.test", ["to@example.test"],
            new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), "final",
            [new("to@example.test", MailSubmissionDeliveryState.Pending, null)]);
        var implicitCommand = new MailMessageMutationCommand(call.Command.AccountId, null, [],
            [new("E44444444444444444444444444444444",
                GatewayEmailPatchCodec.Parse(new JsonObject { ["keywords/$seen"] = true }))], []);
        var implicitResult = new MailMessageMutationResult(MailMessageMutationStatus.Ok, "e1", "e2",
            [], [new("E44444444444444444444444444444444",
                Guid.Parse("44444444-4444-4444-4444-444444444444"), null)], []);
        var result = new MailSubmissionMutationResult(MailSubmissionMutationStatus.Ok, "s1", "s2",
            [new("new", created, null)], [new("#new", createdId, null)],
            [new(ExistingId, null, new(MailSubmissionMutationError.NotFound, null, null, null,
                null, null))], implicitCommand, implicitResult);
        var displayed = GatewaySubmissionSetCodec.Render(call, result);
        Assert.AreEqual(MailOperationKind.MutateSubmissions, displayed.Operation);
        Assert.AreEqual("s2", displayed.Data["newState"]!.GetValue<string>());
        var createdResponse = displayed.Data["created"]!["new"]!;
        Assert.AreEqual($"S{createdId:N}", createdResponse["id"]!.GetValue<string>());
        Assert.IsNull(createdResponse["identityId"]);
        Assert.IsNull(createdResponse["emailId"]);
        Assert.IsNull(createdResponse["envelope"]);
        Assert.AreEqual("queued", createdResponse["deliveryStatus"]!["to@example.test"]!
            ["delivered"]!.GetValue<string>());
        Assert.IsNull(displayed.Data["updated"]![$"S{createdId:N}"]);
        Assert.AreEqual("notFound", displayed.Data["notDestroyed"]![ExistingId]!["type"]!
            .GetValue<string>());
        var additional = GatewaySubmissionSetCodec.RenderAdditional(call, result);
        Assert.HasCount(1, additional);
        Assert.AreEqual(MailOperationKind.MutateMessages, additional[0].Operation);
        Assert.IsNull(additional[0].Data["updated"]!["E44444444444444444444444444444444"]);
    }

    [TestMethod]
    public void GatewayRejectsMalformedSubmissionsAndInconsistentWorkerResults()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = "not-an-account",
            ["create"] = new JsonObject { ["new"] = new JsonObject { ["opaque"] = true } },
        };
        Assert.IsTrue(GatewaySubmissionSetCodec.TryParse(arguments, 1, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        var denied = GatewaySubmissionSetCodec.Render(call,
            new(MailSubmissionMutationStatus.AccountNotFound, null, null, [], [], [], null, null));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
        arguments["unknown"] = true;
        Assert.IsFalse(GatewaySubmissionSetCodec.TryParse(arguments, 1, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
        arguments.Remove("unknown");
        arguments["create"] = new JsonObject { ["bad#key"] = new JsonObject() };
        Assert.IsFalse(GatewaySubmissionSetCodec.TryParse(arguments, 1, out _, out failure));
        arguments["create"] = new JsonObject { ["new"] = "not an object" };
        Assert.IsFalse(GatewaySubmissionSetCodec.TryParse(arguments, 1, out _, out failure));

        Assert.ThrowsExactly<InvalidOperationException>(() => GatewaySubmissionSetCodec.Render(call,
            new(MailSubmissionMutationStatus.Ok, "s1", "s2", [], [], [], null, null)));
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewaySubmissionSetCodec.RenderAdditional(call,
            new(MailSubmissionMutationStatus.Ok, "s1", "s2", [], [], [],
                new MailMessageMutationCommand(call.Command.AccountId, null, [], [], []), null)));
    }
}
