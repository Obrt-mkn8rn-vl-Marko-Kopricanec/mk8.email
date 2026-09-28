using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayMailboxGetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string FolderId = "M22222222222222222222222222222222";

    [TestMethod]
    public void RequestedOrderAndPropertySelectionRemainGatewayOwned()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(FolderId, "N33333333333333333333333333333333", FolderId,
                "M44444444444444444444444444444444"),
            ["properties"] = new JsonArray("name"),
        };
        Assert.IsTrue(GatewayMailboxGetCodec.TryParse(arguments, 5, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual(3, call.Command.FolderIds!.Count);

        var rendered = GatewayMailboxGetCodec.Render(call,
            new MailFolderReadResult(MailFolderReadStatus.Ok, "s7",
                [new MailFolderSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "Inbox", null, "inbox", 0, true, 1, 0, 1, 0, true)]));
        Assert.AreEqual(MailOperationKind.ReadFolders, rendered.Operation);
        Assert.AreEqual(AccountId, rendered.Data["accountId"]!.GetValue<string>());
        Assert.AreEqual("s7", rendered.Data["state"]!.GetValue<string>());
        var list = rendered.Data["list"]!.AsArray();
        Assert.HasCount(1, list);
        CollectionAssert.AreEquivalent(new[] { "id", "name" }, list[0]!.AsObject().Select(item => item.Key).ToArray());
        Assert.AreEqual(FolderId, list[0]!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "N33333333333333333333333333333333", "M44444444444444444444444444444444" },
            rendered.Data["notFound"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public void AccountAuthorizationWinsOverMalformedArguments()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["properties"] = new JsonArray("unknown"),
            ["ids"] = new JsonArray(FolderId),
        };
        Assert.IsTrue(GatewayMailboxGetCodec.TryParse(arguments, 500, out var call, out _));
        Assert.IsNotNull(call);
        Assert.AreEqual("invalidArguments", call.ArgumentFailure);
        Assert.IsTrue(call.Command.CheckAccountOnly);
        Assert.HasCount(0, call.Command.FolderIds!);
        var denied = GatewayMailboxGetCodec.Render(call,
            new MailFolderReadResult(MailFolderReadStatus.AccountNotFound, null, []));
        Assert.AreEqual(MailOperationKind.Failure, denied.Operation);
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void ExcessiveRequestedIdsAreRejectedWithoutSendingThemToWorker()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(FolderId, FolderId),
        };
        Assert.IsTrue(GatewayMailboxGetCodec.TryParse(arguments, 1, out var call, out _));
        Assert.IsNotNull(call);
        Assert.AreEqual("requestTooLarge", call.ArgumentFailure);
        Assert.HasCount(0, call.Command.FolderIds!);
        var response = GatewayMailboxGetCodec.Render(call,
            new MailFolderReadResult(MailFolderReadStatus.Ok, "s0", []));
        Assert.AreEqual("requestTooLarge", response.Data["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void UnknownTopLevelPropertiesFailBeforeAccountParsing()
    {
        Assert.IsFalse(GatewayMailboxGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "not-an-account",
            ["unexpected"] = true,
        }, 500, out var call, out var failure));
        Assert.IsNull(call);
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public async Task AuthorizationOnlyReadsDoNotInitializeMailboxState()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.JmapChanges.CountAsync();
        var reader = scope.ServiceProvider.GetRequiredService<IMailFolderReader>();
        var result = await reader.ReadAsync(new MailFolderReadCommand(fixture.InboxId, [], true), fixture.User,
            CancellationToken.None);
        Assert.AreEqual(MailFolderReadStatus.Ok, result.Status);
        Assert.IsNull(result.State);
        Assert.HasCount(0, result.Folders);
        Assert.AreEqual(before, await database.JmapChanges.CountAsync());
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedFolderCommandsBeforeReceiptOrRead()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var invalid = new JsonObject
        {
            ["accountId"] = fixture.InboxId.ToString(),
            ["folderIds"] = new JsonArray(),
            ["checkAccountOnly"] = false,
            ["unexpected"] = true,
        };
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.ReadFolders, invalid, new Dictionary<string, string>());
        var exception = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);

        invalid.Remove("unexpected");
        invalid["folderIds"] = new JsonArray("not-a-guid");
        exception = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);
    }
}
