using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayMailChangesCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string FolderId = "M22222222222222222222222222222222";

    [TestMethod]
    [DataRow(MailOperationKind.ReadFolderChanges, MailFeature.Messages, true)]
    [DataRow(MailOperationKind.ReadThreadChanges, MailFeature.Messages, false)]
    [DataRow(MailOperationKind.ReadMessageChanges, MailFeature.Messages, false)]
    [DataRow(MailOperationKind.ReadSenderIdentityChanges, MailFeature.Submission, false)]
    [DataRow(MailOperationKind.ReadSubmissionChanges, MailFeature.Submission, false)]
    [DataRow(MailOperationKind.ReadAddressBookChanges, MailFeature.Contacts, false)]
    [DataRow(MailOperationKind.ReadContactChanges, MailFeature.Contacts, true)]
    public void EverySimpleChangeMethodHasTheSameTypedGatewayBoundary(
        MailOperationKind operation, MailFeature feature, bool updatedProperties)
    {
        Assert.IsTrue(MailChangeOperations.TryGetFeature(operation, out var mapped));
        Assert.AreEqual(feature, mapped);
        Assert.IsTrue(GatewayMailChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceState"] = "s0",
        }, operation, out var call, out _));
        Assert.IsNotNull(call);
        Assert.IsTrue(call.Command.AccountReferenceEligible);
        var rendered = GatewayMailChangesCodec.Render(call,
            new MailChangesResult(MailChangesStatus.Ok, "s0", "s1", false, [FolderId], [], []));
        Assert.AreEqual(operation, rendered.Operation);
        Assert.AreEqual(updatedProperties, rendered.Data.ContainsKey("updatedProperties"));
    }

    [TestMethod]
    public void GatewayOwnsArgumentAndResponseShaping()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceState"] = "s0",
            ["maxChanges"] = 2,
        };
        Assert.IsTrue(GatewayMailChangesCodec.TryParse(arguments, MailOperationKind.ReadFolderChanges,
            out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual("s0", call.Command.SinceState);
        Assert.AreEqual(2L, call.Command.MaxChanges);
        var response = GatewayMailChangesCodec.Render(call,
            new MailChangesResult(MailChangesStatus.Ok, "s0", "s42", true,
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
        Assert.IsFalse(GatewayMailChangesCodec.TryParse(arguments, MailOperationKind.ReadFolderChanges,
            out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public void MissingStateAndMalformedAccountHaveDistinctErrors()
    {
        Assert.IsFalse(GatewayMailChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = "not-an-account",
        }, MailOperationKind.ReadFolderChanges, out _, out var missing));
        Assert.AreEqual("invalidArguments", missing);
        Assert.IsFalse(GatewayMailChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = "not-an-account",
            ["sinceState"] = "s0",
        }, MailOperationKind.ReadFolderChanges, out _, out var unknown));
        Assert.AreEqual("accountNotFound", unknown);
    }

    [TestMethod]
    public void WorkerStatusIsRenderedOnlyByGateway()
    {
        Assert.IsTrue(GatewayMailChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceState"] = "s0",
        }, MailOperationKind.ReadFolderChanges, out var call, out _));
        Assert.IsNotNull(call);
        var denied = GatewayMailChangesCodec.Render(call,
            new MailChangesResult(MailChangesStatus.AccountNotFound, null, null, false, [], [], []));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
        var expired = GatewayMailChangesCodec.Render(call,
            new MailChangesResult(MailChangesStatus.CannotCalculateChanges, null, null, false, [], [], []));
        Assert.AreEqual("cannotCalculateChanges", expired.Data["type"]!.GetValue<string>());
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayMailChangesCodec.Render(call,
            new MailChangesResult(MailChangesStatus.Ok, "s999", "s1", false, [], [], [])));
    }

    [TestMethod]
    public async Task ContactChangesRetainPrimaryAndCanonicalAccountPrecedence()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailChangesReader>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.DavCollections.CountAsync();
        var uncanonical = await reader.ReadAsync(MailOperationKind.ReadContactChanges,
            new MailChangesCommand(fixture.InboxId, "s0", null, false), fixture.User, CancellationToken.None);
        Assert.AreEqual(MailChangesStatus.AccountNotSupported, uncanonical.Status);
        Assert.AreEqual(before, await database.DavCollections.CountAsync());
        var unknown = await reader.ReadAsync(MailOperationKind.ReadContactChanges,
            new MailChangesCommand(Guid.NewGuid(), "s0", null, false), fixture.User, CancellationToken.None);
        Assert.AreEqual(MailChangesStatus.AccountNotFound, unknown.Status);
        Assert.AreEqual(before, await database.DavCollections.CountAsync());
        var valid = await reader.ReadAsync(MailOperationKind.ReadAddressBookChanges,
            new MailChangesCommand(fixture.InboxId, "s0", null, true), fixture.User, CancellationToken.None);
        Assert.AreEqual(MailChangesStatus.Ok, valid.Status);
        Assert.IsTrue(await database.DavCollections.CountAsync() > before);
    }

    [TestMethod]
    public void NoncanonicalAccountReferenceIsAttestedForWorkerAuthorization()
    {
        var upper = "AABCDEFABCDEFABCDEFABCDEFABCDEFAB";
        Assert.IsTrue(GatewayMailChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = upper,
            ["sinceState"] = "s0",
        }, MailOperationKind.ReadContactChanges, out var call, out _));
        Assert.IsNotNull(call);
        Assert.IsFalse(call.Command.AccountReferenceEligible);
        var error = GatewayMailChangesCodec.Render(call,
            new MailChangesResult(MailChangesStatus.AccountNotSupported, null, null, false, [], [], []));
        Assert.AreEqual("accountNotSupportedByMethod", error.Data["type"]!.GetValue<string>());
        Assert.IsTrue(GatewayMailChangesCodec.TryParse(new JsonObject
        {
            ["accountId"] = upper,
            ["sinceState"] = "s0",
        }, MailOperationKind.ReadThreadChanges, out var ordinary, out _));
        Assert.IsNotNull(ordinary);
        Assert.IsTrue(ordinary.Command.AccountReferenceEligible);
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
            ["accountReferenceEligible"] = true,
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
        invalid["maxChanges"] = null;
        invalid["accountReferenceEligible"] = false;
        exception = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, exception.Failure.Kind);
    }
}
