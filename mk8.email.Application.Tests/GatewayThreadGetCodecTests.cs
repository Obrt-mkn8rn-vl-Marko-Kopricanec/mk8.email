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
public sealed class GatewayThreadGetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";

    [TestMethod]
    public void GatewayGroupsCollidingOpaqueThreadIdsAndPreservesEmailOrder()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var third = Guid.Parse("33333333-3333-3333-3333-333333333333");
        Assert.AreEqual(JmapId.Thread("c!"), JmapId.Thread("YyE"));
        var id = JmapId.Thread("c!");
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(id, "Tmissing", id),
            ["properties"] = new JsonArray("emailIds"),
        };
        Assert.IsTrue(GatewayThreadGetCodec.TryParse(arguments, 3, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);

        var result = GatewayThreadGetCodec.Render(call,
            new MailThreadReadResult(MailThreadReadStatus.Ok, "s12",
                [new MailThreadEmailSnapshot(first, "c!"),
                 new MailThreadEmailSnapshot(second, "YyE"),
                 new MailThreadEmailSnapshot(third, null)]));
        Assert.AreEqual(MailOperationKind.ReadThreads, result.Operation);
        Assert.AreEqual("s12", result.Data["state"]!.GetValue<string>());
        Assert.HasCount(1, result.Data["list"]!.AsArray());
        var thread = result.Data["list"]![0]!.AsObject();
        Assert.AreEqual(id, thread["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { JmapId.Email(first), JmapId.Email(second) },
            thread["emailIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.AreEqual("Tmissing", result.Data["notFound"]![0]!.GetValue<string>());

        arguments["ids"] = null;
        arguments["properties"] = new JsonArray("id");
        Assert.IsTrue(GatewayThreadGetCodec.TryParse(arguments, 1, out var limited, out _));
        var tooLarge = GatewayThreadGetCodec.Render(limited!,
            new MailThreadReadResult(MailThreadReadStatus.Ok, "s12",
                [new MailThreadEmailSnapshot(first, "c!"),
                 new MailThreadEmailSnapshot(third, null)]));
        Assert.AreEqual("requestTooLarge", tooLarge.Data["type"]!.GetValue<string>());
        Assert.AreEqual(MailOperationKind.Failure, tooLarge.Operation);
        Assert.IsTrue(GatewayThreadGetCodec.TryParse(arguments, 2, out var selected, out _));
        var all = GatewayThreadGetCodec.Render(selected!,
            new MailThreadReadResult(MailThreadReadStatus.Ok, "s12",
                [new MailThreadEmailSnapshot(first, "c!")]));
        Assert.IsFalse(all.Data["list"]![0]!.AsObject().ContainsKey("emailIds"));
    }

    [TestMethod]
    public void GatewayRejectsInvalidThreadSelectionsBeforeDispatch()
    {
        var arguments = new JsonObject { ["accountId"] = AccountId, ["ids"] = new JsonArray("Tvalid") };
        Assert.IsTrue(GatewayThreadGetCodec.TryParse(arguments, 1, out _, out _));
        arguments["ids"] = new JsonArray("Tvalid", "Tvalid");
        Assert.IsFalse(GatewayThreadGetCodec.TryParse(arguments, 1, out _, out var failure));
        Assert.AreEqual("requestTooLarge", failure);
        arguments["ids"] = new JsonArray("invalid/id");
        Assert.IsFalse(GatewayThreadGetCodec.TryParse(arguments, 1, out _, out failure));
        Assert.AreEqual("invalidArguments", failure);
        arguments["ids"] = null;
        arguments["properties"] = new JsonArray("threadId");
        Assert.IsFalse(GatewayThreadGetCodec.TryParse(arguments, 1, out _, out failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public void GatewayOpaqueThreadNormalizationMatchesExistingProjection()
    {
        var arguments = new JsonObject { ["accountId"] = AccountId };
        Assert.IsTrue(GatewayThreadGetCodec.TryParse(arguments, 1, out var call, out _));
        var emailId = Guid.CreateVersion7();
        foreach (var stored in new[] { "", "safe_19", "x/y", "é", new string('a', 254), new string('a', 255) })
        {
            var rendered = GatewayThreadGetCodec.Render(call!,
                new MailThreadReadResult(MailThreadReadStatus.Ok, "s1",
                    [new MailThreadEmailSnapshot(emailId, stored)]));
            Assert.AreEqual(JmapId.Thread(stored), rendered.Data["list"]![0]!["id"]!.GetValue<string>());
        }
        var fallback = GatewayThreadGetCodec.Render(call!,
            new MailThreadReadResult(MailThreadReadStatus.Ok, "s1",
                [new MailThreadEmailSnapshot(emailId, null)]));
        Assert.AreEqual(JmapId.Thread(emailId.ToString("N")),
            fallback.Data["list"]![0]!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task WorkerAuthorizesAccountAndReturnsOrderedNonDeletedDomainRows()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailThreadReader>();
        var missing = await reader.ReadAsync(new MailThreadReadCommand(Guid.NewGuid()),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailThreadReadStatus.AccountNotFound, missing.Status);
        Assert.HasCount(0, missing.Emails);

        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var earlierId = Guid.CreateVersion7();
        var laterId = Guid.CreateVersion7();
        var deletedId = Guid.CreateVersion7();
        database.Emails.Add(new EmailDB
        {
            Id = laterId,
            Sender = "sender@example.test",
            Recipient = fixture.User.Username,
            Subject = "Later",
            Body = "body",
            RawMessage = "later"u8.ToArray(),
            SizeBytes = 5,
            EmailObjectId = laterId.ToString("N"),
            ThreadObjectId = "same-thread",
            ReceivedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            FolderId = fixture.InboxFolderId,
            Uid = 2,
            ModSeq = 1,
        });
        database.Emails.Add(new EmailDB
        {
            Id = deletedId,
            Sender = "sender@example.test",
            Recipient = fixture.User.Username,
            Subject = "Deleted",
            Body = "body",
            RawMessage = "gone"u8.ToArray(),
            SizeBytes = 4,
            EmailObjectId = deletedId.ToString("N"),
            ThreadObjectId = "hidden",
            ReceivedAt = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc),
            FolderId = fixture.InboxFolderId,
            Uid = 3,
            ModSeq = 1,
            IsDeleted = true,
        });
        database.Emails.Add(new EmailDB
        {
            Id = earlierId,
            Sender = "sender@example.test",
            Recipient = fixture.User.Username,
            Subject = "Earlier",
            Body = "body",
            RawMessage = "early"u8.ToArray(),
            SizeBytes = 5,
            EmailObjectId = earlierId.ToString("N"),
            ThreadObjectId = null,
            ReceivedAt = new DateTime(2026, 9, 29, 11, 0, 0, DateTimeKind.Utc),
            FolderId = fixture.InboxFolderId,
            Uid = 1,
            ModSeq = 1,
        });
        await database.SaveChangesAsync();
        var read = await reader.ReadAsync(new MailThreadReadCommand(fixture.InboxId),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailThreadReadStatus.Ok, read.Status);
        Assert.IsNotNull(read.State);
        CollectionAssert.AreEqual(new[] { earlierId, laterId }, read.Emails.Select(email => email.EmailId).ToArray());
        Assert.AreEqual((await database.Emails.SingleAsync(email => email.Id == earlierId)).ThreadObjectId,
            read.Emails[0].StoredThreadId);
        Assert.AreEqual("same-thread", read.Emails[1].StoredThreadId);
        Assert.AreEqual(3, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedThreadCommandBeforeRead()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.ReadThreads, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["properties"] = new JsonArray("id"),
            }, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
