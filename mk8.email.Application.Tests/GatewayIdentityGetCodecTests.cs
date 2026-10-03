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
internal sealed class GatewayIdentityGetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string IdentityId = "I22222222222222222222222222222222";

    [TestMethod]
    public void GatewayOwnsIdentityIdsAddressListsAndPropertySelection()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(IdentityId, "I33333333333333333333333333333333", IdentityId),
            ["properties"] = new JsonArray("email", "replyTo", "bcc"),
        };
        Assert.IsTrue(GatewayIdentityGetCodec.TryParse(arguments, 4, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual(3, call.RequestedIds!.Count);
        Assert.AreEqual(3, call.Command.IdentityIds!.Count);

        var rendered = GatewayIdentityGetCodec.Render(call,
            new MailIdentityReadResult(MailIdentityReadStatus.Ok, "s8",
                [new MailIdentitySnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "Sender", "sender@example.test",
                    new MailIdentityAddressListSnapshot(
                        [new MailIdentityAddressSnapshot("Reply", "reply@example.test")]), null,
                    "Signature", "<p>Signature</p>", true)]));
        Assert.AreEqual(MailOperationKind.ReadSenderIdentities, rendered.Operation);
        Assert.AreEqual("s8", rendered.Data["state"]!.GetValue<string>(), StringComparer.Ordinal);
        var list = rendered.Data["list"]!.AsArray();
        Assert.HasCount(1, list);
        CollectionAssert.AreEquivalent(ExpectedVector1,
            list[0]!.AsObject().Select(property => property.Key).ToArray());
        Assert.AreEqual(IdentityId, list[0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("Reply", list[0]!["replyTo"]![0]!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNull(list[0]!["bcc"]);
        Assert.AreEqual("I33333333333333333333333333333333",
            rendered.Data["notFound"]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void SyntaxAndPropertyErrorsPrecedeLimitsAndAccountLookup()
    {
        Assert.IsFalse(GatewayIdentityGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "unknown",
            ["ids"] = new JsonArray(IdentityId, IdentityId),
            ["properties"] = new JsonArray("unknown"),
        }, 1, out _, out var invalid));
        Assert.AreEqual("invalidArguments", invalid, StringComparer.Ordinal);
        Assert.IsFalse(GatewayIdentityGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "unknown",
            ["ids"] = new JsonArray(IdentityId, IdentityId),
        }, 1, out _, out var tooLarge));
        Assert.AreEqual("requestTooLarge", tooLarge, StringComparer.Ordinal);
        Assert.IsFalse(GatewayIdentityGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "unknown",
        }, 3, out _, out var missing));
        Assert.AreEqual("accountNotFound", missing, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ReaderCreatesDefaultIdentityOnlyForAnAuthorizedAccount()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailIdentityReader>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(0, await database.JmapIdentities.CountAsync().ConfigureAwait(false));
        var missing = await reader.ReadAsync(new MailIdentityReadCommand(Guid.NewGuid(), null),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailIdentityReadStatus.AccountNotFound, missing.Status);
        Assert.AreEqual(0, await database.JmapIdentities.CountAsync().ConfigureAwait(false));
        var valid = await reader.ReadAsync(new MailIdentityReadCommand(fixture.InboxId, null),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailIdentityReadStatus.Ok, valid.Status);
        Assert.HasCount(1, valid.Identities);
        Assert.AreEqual(fixture.InboxId, valid.Identities[0].Id);
        Assert.AreEqual(1, await database.JmapIdentities.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedIdentityReadBeforeMutation()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var malformed = new JsonObject
        {
            ["accountId"] = fixture.InboxId.ToString(),
            ["identityIds"] = new JsonArray(),
            ["unexpected"] = true,
        };
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Submission],
            MailOperationKind.ReadSenderIdentities, malformed, new Dictionary<string, string>(StringComparer.Ordinal));
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        malformed.Remove("unexpected");
        malformed["identityIds"] = new JsonArray("not-a-guid");
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        Assert.AreEqual(0, await database.JmapIdentities.CountAsync().ConfigureAwait(false));
    }
    private static readonly string[] ExpectedVector1 = new[] { "id", "email", "replyTo", "bcc" };
}
