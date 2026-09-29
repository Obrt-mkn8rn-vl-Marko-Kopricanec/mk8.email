using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewaySubmissionQueryCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string SubmissionId = "S22222222222222222222222222222222";

    [TestMethod]
    public void GatewayParsesTypedSubmissionQueryAndRendersWindow()
    {
        var arguments = JsonNode.Parse($$"""
        {"accountId":"{{AccountId}}","filter":{"operator":"AND","conditions":[
          {"undoStatus":"final","after":"2026-09-20T00:00:00Z"},null]},
         "sort":[{"property":"emailId","isAscending":false,"collation":"i;ascii-numeric"}],
         "anchor":"{{SubmissionId}}","position":"ignored","anchorOffset":1,
         "limit":100,"calculateTotal":true}
        """)!.AsObject();
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 2, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual(Guid.Parse("22222222-2222-2222-2222-222222222222"), call.Command.AnchorId);
        Assert.IsTrue(call.Command.AnchorCanMatch);
        Assert.AreEqual(2, call.Command.Limit);
        Assert.AreEqual(MailSubmissionFilterOperator.And, call.Command.Criteria.Filter!.Operator);
        Assert.HasCount(2, call.Command.Criteria.Filter.Conditions!);
        Assert.AreEqual(MailStringCollation.AsciiNumeric, call.Command.Criteria.Sort[0].Collation);
        var response = GatewaySubmissionQueryCodec.RenderQuery(call,
            new MailSubmissionQueryResult(MailSubmissionQueryStatus.Ok, "s2", 1,
                [Guid.Parse("22222222-2222-2222-2222-222222222222")], 7));
        Assert.AreEqual(MailOperationKind.FindSubmissions, response.Operation);
        Assert.AreEqual(7, response.Data["total"]!.GetValue<int>());
        Assert.AreEqual(2, response.Data["limit"]!.GetValue<int>());
        Assert.AreEqual(SubmissionId, response.Data["ids"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayPreservesAccountThenFilterAndSortErrorPrecedence()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = "invalid-account",
            ["filter"] = new JsonObject { ["unsupported"] = true },
            ["sort"] = new JsonArray(new JsonObject { ["property"] = "unknown" }),
        };
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 2, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        Assert.IsTrue(call.Command.CheckAccountOnly);
        Assert.AreEqual("unsupportedFilter", call.DeferredError);
        var denied = GatewaySubmissionQueryCodec.RenderQuery(call,
            new MailSubmissionQueryResult(MailSubmissionQueryStatus.AccountNotFound, null, 0, [], 0));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
        var authorized = GatewaySubmissionQueryCodec.RenderQuery(call,
            new MailSubmissionQueryResult(MailSubmissionQueryStatus.Authorized, null, 0, [], 0));
        Assert.AreEqual("unsupportedFilter", authorized.Data["type"]!.GetValue<string>());
        arguments["filter"] = new JsonObject();
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 2, out call, out _));
        Assert.AreEqual("unsupportedSort", call!.DeferredError);
        arguments["sort"] = new JsonArray(new JsonObject { ["property"] = "sentAt", ["collation"] = null });
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 2, out call, out _));
        Assert.AreEqual("invalidArguments", call!.DeferredError);
    }

    [TestMethod]
    public void GatewayParsesAndRendersSubmissionQueryChanges()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceQueryState"] = "s1",
            ["maxChanges"] = 2,
            ["upToId"] = SubmissionId,
            ["calculateTotal"] = true,
        };
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseChanges(arguments, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.AreEqual(2L, call!.Command.MaxChanges);
        Assert.AreEqual(MailSubmissionSortField.SentAt, call.Command.Criteria.Sort[0].Field);
        var rendered = GatewaySubmissionQueryCodec.RenderChanges(call,
            new MailSubmissionQueryChangesResult(MailSubmissionQueryStatus.Ok, "s2",
                [SubmissionId], [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 4)], 8));
        Assert.AreEqual(MailOperationKind.FindSubmissionChanges, rendered.Operation);
        Assert.AreEqual("s1", rendered.Data["oldQueryState"]!.GetValue<string>());
        Assert.AreEqual(SubmissionId, rendered.Data["removed"]![0]!.GetValue<string>());
        Assert.AreEqual("S33333333333333333333333333333333", rendered.Data["added"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual(4, rendered.Data["added"]![0]!["index"]!.GetValue<int>());
        Assert.AreEqual(8, rendered.Data["total"]!.GetValue<int>());
    }

    [TestMethod]
    public void GatewayIgnoresInactiveWindowArgumentsButRejectsTypedFilterIds()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["anchor"] = SubmissionId,
            ["position"] = "ignored",
            ["filter"] = new JsonObject { ["identityIds"] = new JsonArray("bad/id") },
        };
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 10, out var call, out _));
        Assert.AreEqual("invalidArguments", call!.DeferredError);
        arguments["anchor"] = null;
        Assert.IsFalse(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 10, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public void GatewayDistinguishesCanonicalEmptyGuidAnchorFromUnmatchableOpaqueAnchor()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["anchor"] = "S00000000000000000000000000000000",
        };
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 10, out var canonical, out _));
        Assert.AreEqual(Guid.Empty, canonical!.Command.AnchorId);
        Assert.IsTrue(canonical.Command.AnchorCanMatch);
        arguments["anchor"] = "opaque";
        Assert.IsTrue(GatewaySubmissionQueryCodec.TryParseQuery(arguments, 10, out var unmatchable, out _));
        Assert.AreEqual(Guid.Empty, unmatchable!.Command.AnchorId);
        Assert.IsFalse(unmatchable.Command.AnchorCanMatch);
    }

    [TestMethod]
    public async Task WorkerRejectsExtraTypedQueryFields()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Submission],
            MailOperationKind.FindSubmissions, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["criteria"] = new JsonObject { ["sort"] = new JsonArray() },
                ["checkAccountOnly"] = false,
                ["position"] = 0,
                ["anchorId"] = null,
                ["anchorOffset"] = 0,
                ["limit"] = 10,
                ["extra"] = true,
            }, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
