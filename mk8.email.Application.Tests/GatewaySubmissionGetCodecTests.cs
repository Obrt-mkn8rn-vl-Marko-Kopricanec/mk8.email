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
public sealed class GatewaySubmissionGetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string SubmissionId = "S22222222222222222222222222222222";

    [TestMethod]
    public void GatewayRendersSubmissionEnvelopeAndDeliveryStatus()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(SubmissionId, "S33333333333333333333333333333333"),
            ["properties"] = new JsonArray("envelope", "sendAt", "deliveryStatus", "dsnBlobIds", "mdnBlobIds"),
        };
        Assert.IsTrue(GatewaySubmissionGetCodec.TryParse(arguments, 3, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.IsTrue(call.Command.IncludeDeliveryStatus);
        Assert.HasCount(2, call.Command.SubmissionIds!);

        var snapshot = new MailSubmissionSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "I11111111111111111111111111111111", "E11111111111111111111111111111111",
            "Tthread", new(new("sender@example.test", new Dictionary<string, string> { ["SIZE"] = "12" }), []),
            "sender@example.test", ["recipient@example.test"],
            new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc), "final",
            [new MailSubmissionDeliverySnapshot("recipient@example.test",
                MailSubmissionDeliveryState.PermanentFailure, "bad\r\nrecipient")]);
        var result = GatewaySubmissionGetCodec.Render(call,
            new MailSubmissionReadResult(MailSubmissionReadStatus.Ok, "s8", [snapshot]));
        Assert.AreEqual(MailOperationKind.ReadSubmissions, result.Operation);
        Assert.AreEqual("s8", result.Data["state"]!.GetValue<string>());
        var submission = result.Data["list"]![0]!.AsObject();
        CollectionAssert.AreEquivalent(new[] { "id", "envelope", "sendAt", "deliveryStatus", "dsnBlobIds", "mdnBlobIds" },
            submission.Select(item => item.Key).ToArray());
        Assert.AreEqual("12", submission["envelope"]!["mailFrom"]!["parameters"]!["SIZE"]!.GetValue<string>());
        Assert.AreEqual("2026-09-29T12:34:56Z", submission["sendAt"]!.GetValue<string>());
        Assert.AreEqual("no", submission["deliveryStatus"]!["recipient@example.test"]!["delivered"]!.GetValue<string>());
        Assert.AreEqual("550 5.0.0 bad  recipient",
            submission["deliveryStatus"]!["recipient@example.test"]!["smtpReply"]!.GetValue<string>());
        Assert.HasCount(0, submission["dsnBlobIds"]!.AsArray());
        Assert.HasCount(0, submission["mdnBlobIds"]!.AsArray());
        Assert.AreEqual("S33333333333333333333333333333333",
            result.Data["notFound"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayUsesIndexedEnvelopeWhenOptionalSnapshotIsAbsentAndSkipsUnselectedDelivery()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["properties"] = new JsonArray("envelope"),
        };
        Assert.IsTrue(GatewaySubmissionGetCodec.TryParse(arguments, 2, out var call, out _));
        Assert.IsFalse(call!.Command.IncludeDeliveryStatus);
        var result = GatewaySubmissionGetCodec.Render(call,
            new MailSubmissionReadResult(MailSubmissionReadStatus.Ok, "s1",
                [new MailSubmissionSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "I1", "E1", "T1", null, "sender@example.test",
                    ["recipient@example.test"], DateTime.UtcNow, "final", null)]));
        var submission = result.Data["list"]![0]!.AsObject();
        Assert.IsFalse(submission.ContainsKey("deliveryStatus"));
        Assert.AreEqual("sender@example.test", submission["envelope"]!["mailFrom"]!["email"]!.GetValue<string>());
        Assert.IsNull(submission["envelope"]!["rcptTo"]![0]!["parameters"]);
        Assert.AreEqual("recipient@example.test",
            submission["envelope"]!["rcptTo"]![0]!["email"]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayRejectsInvalidSelectionsAndIncompleteDeliveryResults()
    {
        var arguments = new JsonObject { ["accountId"] = AccountId, ["ids"] = new JsonArray(SubmissionId) };
        Assert.IsTrue(GatewaySubmissionGetCodec.TryParse(arguments, 1, out var call, out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewaySubmissionGetCodec.Render(call!,
            new MailSubmissionReadResult(MailSubmissionReadStatus.Ok, "s1",
                [new MailSubmissionSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "I1", "E1", "T1", null, "sender@example.test", [], DateTime.UtcNow,
                    "final", null)])));
        arguments["ids"] = new JsonArray(SubmissionId, SubmissionId);
        Assert.IsFalse(GatewaySubmissionGetCodec.TryParse(arguments, 1, out _, out var failure));
        Assert.AreEqual("requestTooLarge", failure);
        arguments["ids"] = new JsonArray("invalid/id");
        Assert.IsFalse(GatewaySubmissionGetCodec.TryParse(arguments, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure);
        arguments["ids"] = null;
        arguments["properties"] = new JsonArray("unknown");
        Assert.IsFalse(GatewaySubmissionGetCodec.TryParse(arguments, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public async Task WorkerAuthorizesAccountAndReturnsOnlyRequestedDomainRows()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailSubmissionReader>();
        var missing = await reader.ReadAsync(new MailSubmissionReadCommand(Guid.NewGuid(), null, true),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailSubmissionReadStatus.AccountNotFound, missing.Status);

        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var requestedId = Guid.CreateVersion7();
        var otherId = Guid.CreateVersion7();
        foreach (var id in new[] { requestedId, otherId })
        {
            database.JmapEmailSubmissions.Add(new JmapEmailSubmissionDB
            {
                Id = id,
                SubmissionObjectId = JmapId.Submission(id),
                AccountId = fixture.InboxId,
                IdentityId = JmapId.Identity(fixture.InboxId),
                EmailId = JmapId.Email(id),
                ThreadId = JmapId.Thread(id.ToString("N")),
                QueueId = Guid.CreateVersion7(),
                EnvelopeSender = fixture.User.Username,
                EnvelopeRecipients = ["recipient@example.test"],
                SendAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            });
        }
        await database.SaveChangesAsync();
        var read = await reader.ReadAsync(new MailSubmissionReadCommand(fixture.InboxId, [requestedId], false),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailSubmissionReadStatus.Ok, read.Status);
        Assert.HasCount(1, read.Submissions);
        Assert.AreEqual(requestedId, read.Submissions[0].Id);
        Assert.IsNull(read.Submissions[0].DeliveryRecipients);
        Assert.AreEqual(2, await database.JmapEmailSubmissions.CountAsync());
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedSubmissionCommand()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Submission],
            MailOperationKind.ReadSubmissions, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["submissionIds"] = null,
                ["includeDeliveryStatus"] = "true",
            }, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
