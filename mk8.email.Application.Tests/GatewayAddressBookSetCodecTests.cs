using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayAddressBookSetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string BookId = "D22222222222222222222222222222222";

    [TestMethod]
    public void GatewayOwnsAddressBookPatchSyntaxAndDefaultRendering()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["create"] = new JsonObject { ["new"] = new JsonObject { ["name"] = "New" } },
            ["update"] = new JsonObject
            {
                [BookId] = new JsonObject
                {
                    ["myRights/mayRead"] = true,
                    ["myRights/mayDelete"] = false,
                    ["description"] = null,
                    ["unknown"] = null,
                },
            },
            ["onSuccessSetIsDefault"] = "#new",
        };
        Assert.IsTrue(GatewayAddressBookSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual("new", call.Command.OnSuccessSetIsDefault!.CreatedKey);
        Assert.IsTrue(call.Command.Updates[0].Patch!.Rights!.CheckMayRead);
        Assert.IsFalse(call.Command.Updates[0].Patch!.Rights!.MayDelete);
        var newBook = new MailAddressBookSnapshot(Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "New", null, 0, true, true, true);
        var prior = new MailAddressBookSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Prior", null, 0, false, true, true);
        var rendered = GatewayAddressBookSetCodec.Render(call, new MailAddressBookMutationResult(
            MailAddressBookMutationStatus.Ok, "s1", "s2",
            [new MailAddressBookCreateOutcome("new", newBook, MailAddressBookMutationError.None, null)],
            [new MailAddressBookUpdateOutcome(BookId, prior, MailAddressBookMutationError.None, null)], [],
            [prior, newBook]));
        Assert.AreEqual(MailOperationKind.MutateAddressBooks, rendered.Operation);
        Assert.IsTrue(rendered.Data["created"]!["new"]!["isDefault"]!.GetValue<bool>());
        Assert.IsFalse(rendered.Data["updated"]![BookId]!["isDefault"]!.GetValue<bool>());

        arguments["update"]![BookId] = new JsonObject
        {
            ["myRights"] = new JsonObject { ["mayDelete"] = true },
            ["myRights/mayDelete"] = true,
        };
        Assert.IsTrue(GatewayAddressBookSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 10, out call, out failure));
        Assert.AreEqual("invalidPatch", call!.UpdateErrors[BookId]["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task WorkerRejectsForgedAddressBookTargetsAndDuplicateCreationIdsBeforePersistence()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.DavCollections.CountAsync();
        var malformed = JsonNode.Parse($$"""
            {"accountId":"{{fixture.InboxId}}","accountReferenceEligible":true,
             "ifInState":null,"onDestroyRemoveContents":false,"onSuccessSetIsDefault":null,
             "creates":[{"creationId":"same","hasNonNullShareWith":false,"values":null},
                        {"creationId":"same","hasNonNullShareWith":false,"values":null}],
             "updates":[],"destroys":[]}
            """)!.AsObject();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Contacts],
            MailOperationKind.MutateAddressBooks, malformed, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);

        malformed["creates"] = new JsonArray();
        malformed["updates"] = JsonNode.Parse($$"""
            [{"requestedId":"{{BookId}}","target":
              {"existingId":"22222222-2222-2222-2222-222222222222","createdKey":"same"},
              "patch":null}]
            """);
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        Assert.AreEqual(before, await database.DavCollections.CountAsync());
    }
}
