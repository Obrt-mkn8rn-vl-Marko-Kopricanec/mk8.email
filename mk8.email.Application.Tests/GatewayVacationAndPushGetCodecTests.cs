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
public sealed class GatewayVacationAndPushGetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string SubscriptionId = "P22222222222222222222222222222222";

    [TestMethod]
    public void VacationWireSelectionAndDateRenderingBelongToGateway()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray("missing", "singleton", "missing"),
            ["properties"] = new JsonArray("isEnabled", "fromDate", "textBody"),
        };
        Assert.IsTrue(GatewayVacationGetCodec.TryParse(arguments, 4, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.IsTrue(call.Command.IncludeSingleton);
        Assert.IsTrue(call.Command.IncludeBodies);

        var rendered = GatewayVacationGetCodec.Render(call,
            new MailVacationReadResult(MailVacationReadStatus.Ok, "s9",
                new MailVacationSnapshot(true,
                    new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567),
                    null, "Away", "See you", null)));
        Assert.AreEqual(MailOperationKind.ReadVacationSettings, rendered.Operation);
        var vacation = rendered.Data["list"]![0]!.AsObject();
        CollectionAssert.AreEquivalent(new[] { "id", "isEnabled", "fromDate", "textBody" },
            vacation.Select(property => property.Key).ToArray());
        Assert.AreEqual("2026-09-29T12:34:56.1234567Z", vacation["fromDate"]!.GetValue<string>());
        Assert.AreEqual("See you", vacation["textBody"]!.GetValue<string>());
        Assert.HasCount(1, rendered.Data["notFound"]!.AsArray());
        Assert.AreEqual("missing", rendered.Data["notFound"]![0]!.GetValue<string>());

        arguments["ids"] = new JsonArray("missing");
        arguments["properties"] = new JsonArray("isEnabled");
        Assert.IsTrue(GatewayVacationGetCodec.TryParse(arguments, 4, out var omitted, out _));
        Assert.IsFalse(omitted!.Command.IncludeSingleton);
        Assert.IsFalse(omitted.Command.IncludeBodies);
    }

    [TestMethod]
    public void PushWireSelectionRedactsSecretsAndPreservesNullTypes()
    {
        var arguments = new JsonObject
        {
            ["ids"] = new JsonArray(SubscriptionId, "P33333333333333333333333333333333"),
            ["properties"] = new JsonArray("deviceClientId", "verificationCode", "expires", "types"),
        };
        Assert.IsTrue(GatewayPushSubscriptionGetCodec.TryParse(arguments, 3, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.HasCount(2, call.Command.SubscriptionIds!);
        var rendered = GatewayPushSubscriptionGetCodec.Render(call,
            new MailPushSubscriptionReadResult(MailPushSubscriptionReadStatus.Ok,
                [new MailPushSubscriptionSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "device", null, new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc), null)]));
        Assert.AreEqual(MailOperationKind.ReadNotificationSubscriptions, rendered.Operation);
        var subscription = rendered.Data["list"]![0]!.AsObject();
        Assert.AreEqual("2026-09-29T12:34:56Z", subscription["expires"]!.GetValue<string>());
        Assert.IsNull(subscription["verificationCode"]);
        Assert.IsNull(subscription["types"]);
        Assert.IsFalse(subscription.ContainsKey("url"));
        Assert.IsFalse(subscription.ContainsKey("keys"));
        Assert.AreEqual("P33333333333333333333333333333333",
            rendered.Data["notFound"]![0]!.GetValue<string>());

        arguments["properties"] = new JsonArray("url");
        Assert.IsFalse(GatewayPushSubscriptionGetCodec.TryParse(arguments, 3, out _, out var forbidden));
        Assert.AreEqual("forbidden", forbidden);
        arguments["properties"] = new JsonArray("keys");
        Assert.IsFalse(GatewayPushSubscriptionGetCodec.TryParse(arguments, 3, out _, out forbidden));
        Assert.AreEqual("forbidden", forbidden);
    }

    [TestMethod]
    public async Task VacationReaderRejectsMissingAccountBeforeCreatingSingleton()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailVacationReader>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var missing = await reader.ReadAsync(new MailVacationReadCommand(Guid.NewGuid(), true, true),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailVacationReadStatus.AccountNotFound, missing.Status);
        Assert.AreEqual(0, await database.JmapVacationResponses.CountAsync());
        var valid = await reader.ReadAsync(new MailVacationReadCommand(fixture.InboxId, true, false),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailVacationReadStatus.Ok, valid.Status);
        Assert.IsNotNull(valid.Vacation);
        Assert.IsFalse(valid.Vacation.IsEnabled);
        Assert.AreEqual(1, await database.JmapVacationResponses.CountAsync());
    }

    [TestMethod]
    public async Task PushReaderDeletesOnlyExpiredSubscriptionsForCurrentUser()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailPushSubscriptionReader>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
        {
            Id = Guid.NewGuid(),
            UserId = fixture.User.Id,
            DeviceClientId = "expired",
            Url = "https://expired.example.test/",
            KeysJson = "{}",
            VerificationCode = "secret",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        });
        var retainedId = Guid.NewGuid();
        database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
        {
            Id = retainedId,
            UserId = fixture.User.Id,
            DeviceClientId = "active",
            Url = "https://active.example.test/",
            VerificationCode = "code",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        var otherUserId = Guid.NewGuid();
        database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            DeviceClientId = "other-user-expired",
            Url = "https://other.example.test/",
            VerificationCode = "other-code",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        });
        await database.SaveChangesAsync();
        var read = await reader.ReadAsync(new MailPushSubscriptionReadCommand(null),
            fixture.User, CancellationToken.None);
        Assert.AreEqual(MailPushSubscriptionReadStatus.Ok, read.Status);
        Assert.HasCount(1, read.Subscriptions);
        Assert.AreEqual(retainedId, read.Subscriptions[0].Id);
        Assert.IsNull(read.Subscriptions[0].VerificationCode);
        Assert.AreEqual(2, await database.JmapPushSubscriptions.CountAsync());
        Assert.AreEqual("https://other.example.test/",
            (await database.JmapPushSubscriptions.SingleAsync(subscription => subscription.UserId == otherUserId)).Url);
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedReadsBeforeMutation()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var vacation = new MailOperationCommand([MailFeature.Basic, MailFeature.AutomaticReplies],
            MailOperationKind.ReadVacationSettings, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["includeSingleton"] = false,
                ["includeBodies"] = true,
            }, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(vacation, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        Assert.AreEqual(0, await database.JmapVacationResponses.CountAsync());
        var push = new MailOperationCommand([MailFeature.Basic],
            MailOperationKind.ReadNotificationSubscriptions, new JsonObject
            {
                ["subscriptionIds"] = new JsonArray("invalid-guid"),
            }, new Dictionary<string, string>());
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(push, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
