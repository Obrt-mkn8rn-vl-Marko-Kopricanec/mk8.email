using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayEmailReadCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string FirstId = "E22222222222222222222222222222222";
    private const string SecondId = "E33333333333333333333333333333333";

    [TestMethod]
    public void GetPreservesRequestedOrderAndRendersMissingIds()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ids"] = new JsonArray(SecondId, "opaque", FirstId, SecondId),
            ["properties"] = new JsonArray("id", "subject", "header:Subject:asText"),
            ["bodyProperties"] = new JsonArray("partId", "header:Content-Type"),
            ["fetchTextBodyValues"] = true,
            ["maxBodyValueBytes"] = 100L,
        };
        Assert.IsTrue(GatewayEmailReadCodec.TryParseGet(arguments, 4, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(2, call.Command.MessageIds!.Count);
        Assert.AreEqual(100, call.Projection.MaxBodyValueBytes);
        Assert.IsTrue(call.Projection.FetchTextBodyValues);
        var first = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var second = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var result = new MailMessageReadResult(MailMessageReadStatus.Ok, "s5",
        [
            new(first, Snapshot(first, "one")),
            new(second, Snapshot(second, "two")),
        ]);
        var response = GatewayEmailReadCodec.RenderGet(call, result);
        Assert.AreEqual(MailOperationKind.ReadMessages, response.Operation);
        Assert.AreEqual("s5", response.Data["state"]!.GetValue<string>(), StringComparer.Ordinal);
        CollectionAssert.AreEqual(new[] { SecondId, FirstId }, response.Data["list"]!.AsArray()
            .Select(item => item!["id"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(ExpectedVector1, response.Data["notFound"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public void GetAndParseKeepDistinctPropertyNullAndLimitRules()
    {
        var get = new JsonObject { ["accountId"] = AccountId, ["properties"] = null };
        Assert.IsTrue(GatewayEmailReadCodec.TryParseGet(get, 2, out var call, out _));
        Assert.IsNotNull(call);
        CollectionAssert.Contains(call.Projection.Properties.ToArray(), "id");
        get["ids"] = new JsonArray(FirstId, FirstId, FirstId);
        Assert.IsFalse(GatewayEmailReadCodec.TryParseGet(get, 2, out _, out var failure));
        Assert.AreEqual("requestTooLarge", failure, StringComparer.Ordinal);
        var parse = new JsonObject { ["accountId"] = AccountId, ["blobIds"] = new JsonArray("blob_1") };
        Assert.IsTrue(GatewayEmailReadCodec.TryParseParse(parse, 2, out var parsed, out _));
        Assert.IsNotNull(parsed);
        CollectionAssert.DoesNotContain(parsed.Projection.Properties.ToArray(), "id");
        parse["properties"] = null;
        Assert.IsFalse(GatewayEmailReadCodec.TryParseParse(parse, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
        parse.Remove("properties");
        parse["blobIds"] = null;
        Assert.IsFalse(GatewayEmailReadCodec.TryParseParse(parse, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
    }

    [TestMethod]
    public void GatewayRejectsInvalidProjectionAndInconsistentWorkerResults()
    {
        var arguments = new JsonObject { ["accountId"] = AccountId, ["ids"] = new JsonArray(FirstId) };
        foreach (var property in new[] { "header:Subject:asDate", "header:bad:name", "unknown" })
        {
            arguments["properties"] = new JsonArray(property);
            Assert.IsFalse(GatewayEmailReadCodec.TryParseGet(arguments, 2, out _, out var failure));
            Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
        }
        arguments["properties"] = new JsonArray("header:Subject:asText");
        arguments["maxBodyValueBytes"] = null;
        Assert.IsFalse(GatewayEmailReadCodec.TryParseGet(arguments, 2, out _, out _));
        arguments.Remove("maxBodyValueBytes");
        Assert.IsTrue(GatewayEmailReadCodec.TryParseGet(arguments, 2, out var call, out _));
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Throws<InvalidOperationException>(() => GatewayEmailReadCodec.RenderGet(call!,
            new MailMessageReadResult(MailMessageReadStatus.Ok, "s1",
                [new(id, Snapshot(Guid.Parse("33333333-3333-3333-3333-333333333333"), "unexpected"))])));
        Assert.Throws<InvalidOperationException>(() => GatewayEmailReadCodec.RenderGet(call!,
            new MailMessageReadResult(MailMessageReadStatus.AccountNotFound, null,
                [new(id, Snapshot(id, "unexpected"))])));
    }

    [TestMethod]
    public void ParseRendersEachBlobOutcomeAndRejectsReorderedResults()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["blobIds"] = new JsonArray("blob_1", "blob_2", "blob_3"),
        };
        Assert.IsTrue(GatewayEmailReadCodec.TryParseParse(arguments, 3, out var call, out _));
        Assert.IsNotNull(call);
        var result = new MailMessageParseResult(MailMessageParseStatus.Ok,
        [
            new("blob_1", MailMessageParseItemStatus.Parsed,
                Snapshot(null, "one", "blob_1")),
            new("blob_2", MailMessageParseItemStatus.NotParsable, null),
            new("blob_3", MailMessageParseItemStatus.NotFound, null),
        ]);
        var response = GatewayEmailReadCodec.RenderParse(call, result);
        Assert.AreEqual(MailOperationKind.ParseMessages, response.Operation);
        Assert.AreEqual("one", response.Data["parsed"]!["blob_1"]!["subject"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("blob_2", response.Data["notParsable"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("blob_3", response.Data["notFound"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => GatewayEmailReadCodec.RenderParse(call,
            result with { Items = result.Items.Reverse().ToArray() }));
    }

    [TestMethod]
    public async Task WorkerRejectsWireProjectionAndMissingTypedTextSelectionBeforeReading()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.ReadMessages, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["messageIds"] = null,
                ["projection"] = new JsonObject
                {
                    ["properties"] = new JsonArray("id"),
                    ["bodyProperties"] = new JsonArray(),
                    ["fetchTextBodyValues"] = false,
                    ["fetchHtmlBodyValues"] = false,
                    ["fetchAllBodyValues"] = false,
                    ["maxBodyValueBytes"] = 0,
                    ["extra"] = true,
                },
            }, new Dictionary<string, string>(StringComparer.Ordinal));
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        command.Arguments.Remove("projection");
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        command.Arguments["includeText"] = "false";
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        command.Arguments["includeText"] = false;
        var accepted = await processor.ExecuteAsync(command, fixture.User, null).ConfigureAwait(false);
        Assert.AreEqual(MailOperationKind.ReadMessages, accepted.Response.Operation);
    }

    private static MailMessageSnapshot Snapshot(Guid? id, string subject, string? uploaded = null) =>
        new(id ?? Guid.Parse("44444444-4444-4444-4444-444444444444"), uploaded, null, 20,
            id is { } storedId ? new(storedId, Guid.Parse("55555555-5555-5555-5555-555555555555"),
                "thread", [], 20, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) : null,
            [new("Subject"u8.ToArray(), Encoding.UTF8.GetBytes(" " + subject + "\r\n"))], null, []);
    private static readonly string[] ExpectedVector1 = new[] { "opaque" };
}
