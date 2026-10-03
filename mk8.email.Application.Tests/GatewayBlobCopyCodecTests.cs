using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayBlobCopyCodecTests
{
    private const string SourceAccountId = "A11111111111111111111111111111111";
    private const string TargetAccountId = "A22222222222222222222222222222222";
    private const string SourceBlobId = "U44444444444444444444444444444444";
    private const string MissingBlobId = "U66666666666666666666666666666666";
    private const string CopiedBlobId = "U55555555555555555555555555555555";

    [TestMethod]
    public void GatewayParsesAndRendersPerBlobCopyOutcomes()
    {
        var arguments = new JsonObject
        {
            ["fromAccountId"] = SourceAccountId,
            ["accountId"] = TargetAccountId,
            ["blobIds"] = new JsonArray(SourceBlobId, MissingBlobId, SourceBlobId),
        };
        Assert.IsTrue(GatewayBlobCopyCodec.TryParse(arguments, 3, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.FromAccountId);
        Assert.AreEqual(Guid.Parse("22222222-2222-2222-2222-222222222222"), call.Command.AccountId);
        Assert.HasCount(3, call.Command.BlobIds);
        var rendered = GatewayBlobCopyCodec.Render(call,
            new MailBlobCopyResult(MailBlobCopyStatus.Ok,
                [new MailBlobCopyItemResult(SourceBlobId, MailBlobCopyItemStatus.Copied, CopiedBlobId),
                 new MailBlobCopyItemResult(MissingBlobId, MailBlobCopyItemStatus.NotFound, null)]));
        Assert.AreEqual(MailOperationKind.CopyBinaryObjects, rendered.Operation);
        Assert.AreEqual(SourceAccountId, rendered.Data["fromAccountId"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(TargetAccountId, rendered.Data["accountId"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(CopiedBlobId, rendered.Data["copied"]![SourceBlobId]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("notFound", rendered.Data["notCopied"]![MissingBlobId]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.HasCount(1, rendered.Data["copied"]!.AsObject());
        Assert.HasCount(1, rendered.Data["notCopied"]!.AsObject());
    }

    [TestMethod]
    public void GatewayPreservesSourceThenTargetAccountErrorPrecedence()
    {
        var arguments = new JsonObject
        {
            ["fromAccountId"] = "invalid-source",
            ["accountId"] = "invalid-target",
            ["blobIds"] = new JsonArray(),
        };
        Assert.IsTrue(GatewayBlobCopyCodec.TryParse(arguments, 1, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.FromAccountId);
        Assert.AreEqual(Guid.Empty, call.Command.AccountId);
        var sourceError = GatewayBlobCopyCodec.Render(call,
            new MailBlobCopyResult(MailBlobCopyStatus.FromAccountNotFound, []));
        Assert.AreEqual("fromAccountNotFound", sourceError.Data["type"]!.GetValue<string>(), StringComparer.Ordinal);

        arguments["fromAccountId"] = SourceAccountId;
        Assert.IsTrue(GatewayBlobCopyCodec.TryParse(arguments, 1, out call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        var targetError = GatewayBlobCopyCodec.Render(call,
            new MailBlobCopyResult(MailBlobCopyStatus.AccountNotFound, []));
        Assert.AreEqual("accountNotFound", targetError.Data["type"]!.GetValue<string>(), StringComparer.Ordinal);
        arguments["accountId"] = SourceAccountId;
        Assert.IsFalse(GatewayBlobCopyCodec.TryParse(arguments, 1, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
        arguments["accountId"] = TargetAccountId;
        arguments["blobIds"] = new JsonArray("invalid/id");
        Assert.IsFalse(GatewayBlobCopyCodec.TryParse(arguments, 1, out _, out failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
        arguments["blobIds"] = new JsonArray(SourceBlobId, MissingBlobId);
        Assert.IsFalse(GatewayBlobCopyCodec.TryParse(arguments, 1, out _, out failure));
        Assert.AreEqual("requestTooLarge", failure, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerAuthorizesSourceBeforeTargetAndRejectsMalformedCommands()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMailBlobCopyService>();
        var missingSource = await service.CopyAsync(new MailBlobCopyCommand(Guid.NewGuid(), fixture.InboxId, []),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailBlobCopyStatus.FromAccountNotFound, missingSource.Status);
        var missingTarget = await service.CopyAsync(new MailBlobCopyCommand(fixture.InboxId, Guid.NewGuid(), []),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailBlobCopyStatus.AccountNotFound, missingTarget.Status);

        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var malformed = new MailOperationCommand([MailFeature.Basic], MailOperationKind.CopyBinaryObjects,
            new JsonObject
            {
                ["fromAccountId"] = fixture.InboxId.ToString(),
                ["accountId"] = Guid.NewGuid().ToString(),
                ["blobIds"] = new JsonArray(SourceBlobId),
                ["extra"] = true,
            }, new Dictionary<string, string>(StringComparer.Ordinal));
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(malformed, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(0, await database.JmapBlobs.CountAsync().ConfigureAwait(false));
    }
}
