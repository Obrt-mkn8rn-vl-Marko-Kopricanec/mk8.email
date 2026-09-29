using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayIdentitySetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string IdentityId = "I22222222222222222222222222222222";

    [TestMethod]
    public void GatewayRejectsMalformedPatchesAndPreservesInvalidPropertyNames()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["update"] = new JsonObject { [IdentityId] = new JsonObject { ["unknown"] = true } },
        };
        Assert.IsTrue(GatewayIdentitySetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.IsNull(call.Command.Updates[0].Patch);
        Assert.AreEqual("unknown", call.UpdateErrors[IdentityId]["properties"]![0]!.GetValue<string>());
        var result = GatewayIdentitySetCodec.Render(call, new MailIdentityMutationResult(
            MailIdentityMutationStatus.Ok, "s0", "s0", [],
            [new MailIdentityUpdateOutcome(IdentityId, null, MailIdentityMutationError.Skipped, null)], []));
        Assert.AreEqual("invalidProperties", result.Data["notUpdated"]![IdentityId]!["type"]!
            .GetValue<string>());

        arguments["update"]![IdentityId] = new JsonObject { ["unknown"] = null };
        Assert.IsTrue(GatewayIdentitySetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out call, out failure));
        Assert.IsNotNull(call!.Command.Updates[0].Patch);

        arguments["update"]![IdentityId] = new JsonObject { ["name/child"] = "bad" };
        Assert.IsTrue(GatewayIdentitySetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out call, out failure));
        Assert.AreEqual("invalidPatch", call!.UpdateErrors[IdentityId]["type"]!.GetValue<string>());
        arguments["update"]![IdentityId] = new JsonObject { ["name"] = "valid" };
        Assert.IsTrue(GatewayIdentitySetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out call, out failure));
        Assert.Throws<InvalidOperationException>(() => GatewayIdentitySetCodec.Render(call!,
            new MailIdentityMutationResult(MailIdentityMutationStatus.Ok, "s0", "s0", [],
                [new MailIdentityUpdateOutcome(IdentityId, null, MailIdentityMutationError.Skipped, null)], [])));
    }

    [TestMethod]
    public async Task WorkerRejectsForgedMutationTargetsAndDuplicateCreationIdsBeforePersistence()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var malformed = JsonNode.Parse($$"""
            {"accountId":"{{fixture.InboxId}}","ifInState":null,
             "creates":[{"creationId":"same","values":null},{"creationId":"same","values":null}],
             "updates":[],"destroys":[]}
            """)!.AsObject();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Submission],
            MailOperationKind.MutateSenderIdentities, malformed, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);

        malformed["creates"] = new JsonArray();
        malformed["updates"] = JsonNode.Parse($$"""
            [{"requestedId":"{{IdentityId}}","target":
              {"existingId":"22222222-2222-2222-2222-222222222222","createdKey":"same"},
              "patch":null}]
            """);
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        Assert.AreEqual(0, await database.JmapIdentities.CountAsync());
    }
}
