using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayPushSubscriptionSetCodecTests
{
    private const string SubscriptionId = "P22222222222222222222222222222222";

    [TestMethod]
    public void GatewayOwnsPushPatchSyntaxAndNormalizedSetResponse()
    {
        var arguments = new JsonObject
        {
            ["create"] = new JsonObject
            {
                ["new"] = new JsonObject
                {
                    ["deviceClientId"] = "mobile",
                    ["url"] = "https://1.1.1.1/push",
                },
            },
            ["update"] = new JsonObject
            {
                [SubscriptionId] = new JsonObject
                {
                    ["keys/p256dh"] = "same-key",
                    ["types"] = new JsonArray("Email", "Mailbox"),
                    ["verificationCode"] = null,
                },
            },
        };
        Assert.IsTrue(GatewayPushSubscriptionSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.IsTrue(call.Command.Updates[0].Patch!.AssertKeyP256dh);
        Assert.AreEqual("same-key", call.Command.Updates[0].Patch!.KeyP256dh);
        var createdId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var expiry = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var rendered = GatewayPushSubscriptionSetCodec.Render(call,
            new MailPushSubscriptionMutationResult(
                [new MailPushSubscriptionCreateOutcome("new", createdId,
                    MailPushSubscriptionMutationError.None, expiry, null, null)],
                [new MailPushSubscriptionUpdateOutcome(SubscriptionId,
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    MailPushSubscriptionMutationError.None, null, null)], []));
        Assert.AreEqual(MailOperationKind.MutateNotificationSubscriptions, rendered.Operation);
        Assert.AreEqual($"P{createdId:N}", rendered.Data["created"]!["new"]!["id"]!.GetValue<string>());
        Assert.AreEqual("2026-10-01T00:00:00Z",
            rendered.Data["created"]!["new"]!["expires"]!.GetValue<string>());
        Assert.IsTrue(rendered.Data["created"]!["new"]!.AsObject().ContainsKey("keys"));
        Assert.IsNull(rendered.Data["updated"]![SubscriptionId]);

        arguments["update"]![SubscriptionId] = new JsonObject
        {
            ["keys"] = new JsonObject { ["auth"] = "x", ["p256dh"] = "y" },
            ["keys/auth"] = "x",
        };
        Assert.IsTrue(GatewayPushSubscriptionSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out call, out failure));
        Assert.AreEqual("invalidPatch", call!.UpdateErrors[SubscriptionId]["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task WorkerRejectsDuplicatePushCreationIdsBeforePersistence()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.JmapPushSubscriptions.CountAsync();
        var malformed = JsonNode.Parse("""
            {"creates":[{"creationId":"same","values":null},{"creationId":"same","values":null}],
             "updates":[],"destroys":[]}
            """)!.AsObject();
        var command = new MailOperationCommand([MailFeature.Basic],
            MailOperationKind.MutateNotificationSubscriptions, malformed,
            new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);

        malformed["creates"] = new JsonArray();
        malformed["updates"] = JsonNode.Parse("""
            [{"requestedId":"P22222222222222222222222222222222",
              "target":{"existingId":"33333333-3333-3333-3333-333333333333","createdKey":null},
              "patch":null}]
            """);
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        Assert.AreEqual(before, await database.JmapPushSubscriptions.CountAsync());
    }

    [TestMethod]
    public async Task MissingNestedKeyRemovalIsAnUnchangedAssertion()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        var id = Guid.CreateVersion7();
        var wireId = $"P{id:N}";
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
            {
                Id = id,
                SubscriptionObjectId = wireId,
                UserId = fixture.User.Id,
                DeviceClientId = "patch-test",
                Url = "https://push.example.test/endpoint",
                KeysJson = "{\"p256dh\":\"stored\",\"auth\":\"stored\"}",
                VerificationCode = "secret",
                ExpiresAt = DateTime.UtcNow.AddDays(2),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await database.SaveChangesAsync();
        }
        var accepted = await InvokePatchAsync(new JsonObject { ["keys/unknown"] = null });
        Assert.IsNull(accepted["notUpdated"]);
        Assert.IsTrue(accepted["updated"]!.AsObject().ContainsKey(wireId));
        var changed = await InvokePatchAsync(new JsonObject { ["keys/unknown"] = "new" });
        Assert.AreEqual("invalidProperties", changed["notUpdated"]![wireId]!["type"]!.GetValue<string>());
        Assert.AreEqual("keys", changed["notUpdated"]![wireId]!["properties"]![0]!.GetValue<string>());

        async Task<JsonObject> InvokePatchAsync(JsonObject patch)
        {
            var response = await fixture.InvokeAsync(new JsonObject
            {
                ["using"] = new JsonArray("urn:ietf:params:jmap:core"),
                ["methodCalls"] = new JsonArray(new JsonArray("PushSubscription/set",
                    new JsonObject { ["update"] = new JsonObject { [wireId] = patch } }, "p1")),
            });
            return response["methodResponses"]![0]![1]!.AsObject();
        }
    }
}
