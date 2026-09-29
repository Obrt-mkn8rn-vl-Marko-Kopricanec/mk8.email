using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayContactCardSetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string BookId = "D22222222222222222222222222222222";
    private const string CardId = "C33333333333333333333333333333333";

    [TestMethod]
    public void GatewayOwnsContactPatchAndCreatedCardNormalization()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["create"] = new JsonObject
            {
                ["new"] = new JsonObject
                {
                    ["@type"] = "Card",
                    ["version"] = "1.0",
                    ["uid"] = "new-uid",
                    ["addressBookIds"] = new JsonObject { ["#book"] = true },
                },
            },
            ["update"] = new JsonObject { [CardId] = new JsonObject { ["name/full"] = "Revised" } },
        };
        var known = new Dictionary<string, string> { ["book"] = BookId };
        Assert.IsTrue(GatewayContactCardSetCodec.TryParse(arguments, known, 10, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            call.Command.Creates[0].AddressBookId);
        Assert.AreEqual(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            call.Command.AddressBookAliases["#book"]);
        Assert.AreEqual("full", call.Command.Updates[0].Patch![0].Path[1]);
        var stored = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = "new-uid",
            ["name"] = new JsonObject { ["full"] = "New" },
        };
        var rendered = GatewayContactCardSetCodec.Render(call, new MailContactMutationResult(
            MailContactMutationStatus.Ok, "s1", "s2",
            [new MailContactCreateOutcome("new", Guid.Parse("44444444-4444-4444-4444-444444444444"),
                ApplicationValueCodec.Encode(stored), MailContactMutationError.None, null)],
            [new MailContactUpdateOutcome(CardId, null, null, null,
                MailContactMutationError.NotFound, null)], []));
        Assert.AreEqual(MailOperationKind.MutateContacts, rendered.Operation);
        Assert.AreEqual("New", rendered.Data["created"]!["new"]!["name"]!["full"]!.GetValue<string>());
        Assert.AreEqual("C44444444444444444444444444444444",
            rendered.Data["created"]!["new"]!["id"]!.GetValue<string>());
        Assert.AreEqual("notFound", rendered.Data["notUpdated"]![CardId]!["type"]!.GetValue<string>());

        arguments["update"]![CardId] = new JsonObject { ["name"] = new JsonObject(), ["name/full"] = "x" };
        Assert.IsTrue(GatewayContactCardSetCodec.TryParse(arguments, known, 10, out call, out failure));
        Assert.IsNull(call!.Command.Updates[0].Patch);
    }

    [TestMethod]
    public async Task WorkerRejectsForgedContactTargetsBeforePersistence()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.DavResources.CountAsync();
        var mutation = new MailContactMutationCommand(fixture.InboxId, true, true, null,
            new Dictionary<string, Guid>(), [],
            [new MailContactUpdate(CardId,
                new MailContactTarget(Guid.Parse("44444444-4444-4444-4444-444444444444"), null), null)], []);
        var data = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .AsObject();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Contacts],
            MailOperationKind.MutateContacts, data, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        Assert.AreEqual(before, await database.DavResources.CountAsync());
    }
}
