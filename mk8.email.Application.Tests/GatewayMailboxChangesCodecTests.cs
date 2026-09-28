using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayMailboxChangesCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string FolderId = "M22222222222222222222222222222222";

    [TestMethod]
    public void GatewayOwnsArgumentAndResponseShaping()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceState"] = "s0",
            ["maxChanges"] = 2,
        };
        Assert.IsTrue(GatewayMailboxChangesCodec.TryParse(arguments, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual("s0", call.Command.SinceState);
        Assert.AreEqual(2L, call.Command.MaxChanges);
        var response = GatewayMailboxChangesCodec.Render(call,
            new MailFolderChangesResult(MailFolderChangesStatus.Ok, "s0", "s42", true,
                [FolderId], [], []));
        Assert.AreEqual(MailOperationKind.ReadFolderChanges, response.Operation);
        Assert.AreEqual(AccountId, response.Data["accountId"]!.GetValue<string>());
        Assert.AreEqual("s42", response.Data["newState"]!.GetValue<string>());
        Assert.IsTrue(response.Data["hasMoreChanges"]!.GetValue<bool>());
        Assert.AreEqual(FolderId, response.Data["created"]![0]!.GetValue<string>());
        Assert.IsNull(response.Data["updatedProperties"]);
    }

    [TestMethod]
    [DataRow("maxChanges", 0)]
    [DataRow("maxChanges", -1)]
    [DataRow("maxChanges", 9_007_199_254_740_992L)]
    [DataRow("extra", 1)]
    public void InvalidPropertiesAndLimitsAreRejected(string key, long value)
    {
        var arguments = new JsonObject { ["accountId"] = AccountId, ["sinceState"] = "s0", [key] = value };
        Assert.IsFalse(GatewayMailboxChangesCodec.TryParse(arguments, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public void MissingStateAndMalformedAccountHaveDistinctErrors()
    {
        Assert.IsFalse(GatewayMailboxChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = "not-an-account",
        }, out _, out var missing));
        Assert.AreEqual("invalidArguments", missing);
        Assert.IsFalse(GatewayMailboxChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = "not-an-account",
            ["sinceState"] = "s0",
        }, out _, out var unknown));
        Assert.AreEqual("accountNotFound", unknown);
    }

    [TestMethod]
    public void WorkerStatusIsRenderedOnlyByGateway()
    {
        Assert.IsTrue(GatewayMailboxChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceState"] = "s0",
        }, out var call, out _));
        Assert.IsNotNull(call);
        var denied = GatewayMailboxChangesCodec.Render(call,
            new MailFolderChangesResult(MailFolderChangesStatus.AccountNotFound, null, null, false, [], [], []));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
        var expired = GatewayMailboxChangesCodec.Render(call,
            new MailFolderChangesResult(MailFolderChangesStatus.CannotCalculateChanges, null, null, false, [], [], []));
        Assert.AreEqual("cannotCalculateChanges", expired.Data["type"]!.GetValue<string>());
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayMailboxChangesCodec.Render(call,
            new MailFolderChangesResult(MailFolderChangesStatus.Ok, "s999", "s1", false, [], [], [])));
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedChangesBeforeReceiptOrRead()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var invalid = new JsonObject
        {
            ["accountId"] = fixture.InboxId.ToString(),
            ["sinceState"] = "s0",
            ["maxChanges"] = null,
            ["unexpected"] = true,
        };
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.ReadFolderChanges, invalid, new Dictionary<string, string>());
        var exception = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);
        invalid.Remove("unexpected");
        invalid["maxChanges"] = "not-a-number";
        exception = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);
    }
}
