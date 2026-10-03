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
internal sealed class GatewayAddressBookGetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string BookId = "D22222222222222222222222222222222";

    [TestMethod]
    public void GatewayOwnsBookIdsRightsAndPropertySelection()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(BookId, "D33333333333333333333333333333333", BookId),
            ["properties"] = new JsonArray("name", "myRights"),
        };
        Assert.IsTrue(GatewayAddressBookGetCodec.TryParse(arguments, 4, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual(3, call.RequestedIds!.Count);
        Assert.AreEqual(3, call.Command.BookIds!.Count);
        var rendered = GatewayAddressBookGetCodec.Render(call,
            new MailAddressBookReadResult(MailAddressBookReadStatus.Ok, "s8",
                [new MailAddressBookSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "Personal", "private", 2, true, true, true)]));
        Assert.AreEqual(MailOperationKind.ReadAddressBooks, rendered.Operation);
        Assert.AreEqual("s8", rendered.Data["state"]!.GetValue<string>(), StringComparer.Ordinal);
        var list = rendered.Data["list"]!.AsArray();
        Assert.HasCount(1, list);
        CollectionAssert.AreEquivalent(ExpectedVector1,
            list[0]!.AsObject().Select(property => property.Key).ToArray());
        Assert.AreEqual(BookId, list[0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsFalse(list[0]!["myRights"]!["mayDelete"]!.GetValue<bool>());
        Assert.AreEqual("D33333333333333333333333333333333",
            rendered.Data["notFound"]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void LimitAndSyntaxErrorsWinBeforeAccountLookup()
    {
        Assert.IsFalse(GatewayAddressBookGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "unknown",
            ["ids"] = new JsonArray(BookId, BookId),
            ["properties"] = new JsonArray("unknown"),
        }, 1, out _, out var tooLarge));
        Assert.AreEqual("requestTooLarge", tooLarge, StringComparer.Ordinal);
        Assert.IsFalse(GatewayAddressBookGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "unknown",
            ["ids"] = new JsonArray(42),
        }, 3, out _, out var malformed));
        Assert.AreEqual("invalidArguments", malformed, StringComparer.Ordinal);
        Assert.IsFalse(GatewayAddressBookGetCodec.TryParse(new JsonObject
        {
            ["accountId"] = "unknown",
        }, 3, out _, out var account));
        Assert.AreEqual("accountNotFound", account, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task PrimaryContactAccountRulePrecedesDefaultBookCreation()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMailAddressBookReader>();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var before = await database.DavCollections.CountAsync().ConfigureAwait(false);
        var denied = await reader.ReadAsync(new MailAddressBookReadCommand(fixture.InboxId, null, false),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailAddressBookReadStatus.AccountNotSupported, denied.Status);
        Assert.AreEqual(before, await database.DavCollections.CountAsync().ConfigureAwait(false));
        var missing = await reader.ReadAsync(new MailAddressBookReadCommand(Guid.NewGuid(), null, false),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailAddressBookReadStatus.AccountNotFound, missing.Status);
        var valid = await reader.ReadAsync(new MailAddressBookReadCommand(fixture.InboxId, null, true),
            fixture.User, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailAddressBookReadStatus.Ok, valid.Status);
        Assert.IsTrue(valid.Books.Count > 0);
        Assert.IsTrue(await database.DavCollections.CountAsync().ConfigureAwait(false) > before);
    }

    [TestMethod]
    public async Task WorkerRejectsMalformedTypedBookReadBeforeReceiptOrMutation()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var malformed = new JsonObject
        {
            ["accountId"] = fixture.InboxId.ToString(),
            ["bookIds"] = new JsonArray(),
            ["accountReferenceEligible"] = true,
            ["unexpected"] = true,
        };
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Contacts],
            MailOperationKind.ReadAddressBooks, malformed, new Dictionary<string, string>(StringComparer.Ordinal));
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        malformed.Remove("unexpected");
        malformed["bookIds"] = new JsonArray("not-a-guid");
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
    private static readonly string[] ExpectedVector1 = new[] { "id", "name", "myRights" };
}
