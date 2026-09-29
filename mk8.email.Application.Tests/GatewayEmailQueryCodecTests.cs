using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayEmailQueryCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string EmailId = "E22222222222222222222222222222222";

    [TestMethod]
    public void GatewayParsesTypedMessageQueryAndRendersWindow()
    {
        var arguments = JsonNode.Parse($$"""
        {"accountId":"{{AccountId}}","filter":{"operator":"AND","conditions":[
          {"inMailbox":"M33333333333333333333333333333333","hasKeyword":"$FLAGGED",
           "before":"2026-09-20T00:00:00Z","header":["X-Test","needle"],
           "inMailboxOtherThan":["opaque"]},null]},
         "sort":[{"property":"subject","isAscending":false,"collation":"i;ascii-numeric"}],
         "collapseThreads":true,"anchor":"{{EmailId}}","position":"ignored",
         "anchorOffset":1,"limit":100,"calculateTotal":true}
        """)!.AsObject();
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseQuery(arguments, 2, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual(EmailId, call.Command.AnchorId);
        Assert.AreEqual(2, call.Command.Limit);
        Assert.IsTrue(call.Command.Criteria.CollapseThreads);
        Assert.AreEqual(MailMessageFilterOperator.And, call.Command.Criteria.Filter!.Operator);
        Assert.HasCount(2, call.Command.Criteria.Filter.Conditions!);
        var terms = call.Command.Criteria.Filter.Conditions![0].Terms!;
        Assert.AreEqual("$flagged", terms.Single(term => term.Field == MailMessageFilterField.HasKeyword).Text);
        Assert.AreEqual(DateTimeKind.Utc, terms.Single(term => term.Field == MailMessageFilterField.Before).UtcDate!.Value.Kind);
        Assert.AreEqual("needle", terms.Single(term => term.Field == MailMessageFilterField.Header).HeaderText);
        CollectionAssert.AreEqual(new[] { "opaque" },
            terms.Single(term => term.Field == MailMessageFilterField.InMailboxOtherThan).Values!.ToArray());
        Assert.AreEqual(MailStringCollation.AsciiNumeric, call.Command.Criteria.Sort[0].Collation);
        var response = GatewayEmailQueryCodec.RenderQuery(call,
            new MailMessageQueryResult(MailMessageQueryStatus.Ok, "s2", 1,
                [Guid.Parse("22222222-2222-2222-2222-222222222222")], 7));
        Assert.AreEqual(MailOperationKind.FindMessages, response.Operation);
        Assert.AreEqual(7, response.Data["total"]!.GetValue<int>());
        Assert.AreEqual(2, response.Data["limit"]!.GetValue<int>());
        Assert.AreEqual(EmailId, response.Data["ids"]![0]!.GetValue<string>());
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
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseQuery(arguments, 2, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        Assert.IsTrue(call.Command.CheckAccountOnly);
        Assert.AreEqual("unsupportedFilter", call.DeferredError);
        var denied = GatewayEmailQueryCodec.RenderQuery(call,
            new MailMessageQueryResult(MailMessageQueryStatus.AccountNotFound, null, 0, [], 0));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>());
        var authorized = GatewayEmailQueryCodec.RenderQuery(call,
            new MailMessageQueryResult(MailMessageQueryStatus.Authorized, null, 0, [], 0));
        Assert.AreEqual("unsupportedFilter", authorized.Data["type"]!.GetValue<string>());
        arguments["filter"] = new JsonObject();
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseQuery(arguments, 2, out call, out _));
        Assert.AreEqual("unsupportedSort", call!.DeferredError);
        arguments["sort"] = new JsonArray(new JsonObject { ["property"] = "subject", ["collation"] = null });
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseQuery(arguments, 2, out call, out _));
        Assert.AreEqual("invalidArguments", call!.DeferredError);
    }

    [TestMethod]
    public void GatewayParsesAndRendersMessageQueryChanges()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceQueryState"] = "s1",
            ["maxChanges"] = 2,
            ["upToId"] = EmailId,
            ["calculateTotal"] = true,
        };
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseChanges(arguments, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.AreEqual(2L, call!.Command.MaxChanges);
        Assert.AreEqual(MailMessageSortField.ReceivedAt, call.Command.Criteria.Sort[0].Field);
        var rendered = GatewayEmailQueryCodec.RenderChanges(call,
            new MailMessageQueryChangesResult(MailMessageQueryStatus.Ok, "s2", [EmailId],
                [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 4)], 8));
        Assert.AreEqual(MailOperationKind.FindMessageChanges, rendered.Operation);
        Assert.AreEqual("s1", rendered.Data["oldQueryState"]!.GetValue<string>());
        Assert.AreEqual(EmailId, rendered.Data["removed"]![0]!.GetValue<string>());
        Assert.AreEqual("E33333333333333333333333333333333", rendered.Data["added"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual(4, rendered.Data["added"]![0]!["index"]!.GetValue<int>());
        Assert.AreEqual(8, rendered.Data["total"]!.GetValue<int>());
    }

    [TestMethod]
    public void GatewayRejectsMalformedTermsAndHonorsInactiveWindowArgument()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["anchor"] = EmailId,
            ["position"] = "ignored",
            ["filter"] = new JsonObject { ["header"] = new JsonArray("bad:name") },
        };
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseQuery(arguments, 10, out var call, out _));
        Assert.AreEqual("invalidArguments", call!.DeferredError);
        arguments["anchor"] = null;
        Assert.IsFalse(GatewayEmailQueryCodec.TryParseQuery(arguments, 10, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
    }

    [TestMethod]
    public void GatewayTypesEverySupportedMessageFilterField()
    {
        var arguments = JsonNode.Parse($$$"""
        {"accountId":"{{{AccountId}}}","filter":{
          "inMailbox":"M33333333333333333333333333333333",
          "inMailboxOtherThan":[],"before":"2026-09-20T00:00:00Z",
          "after":"2026-09-19T00:00:00Z","minSize":0,"maxSize":100,
          "allInThreadHaveKeyword":"$seen","someInThreadHaveKeyword":"$flagged",
          "noneInThreadHaveKeyword":"$draft","hasKeyword":"$seen",
          "notKeyword":"$junk","hasAttachment":false,"text":"needle",
          "from":"sender","to":"recipient","cc":"copied","bcc":"hidden",
          "subject":"topic","body":"message","header":["X-Test"]}}
        """)!.AsObject();
        Assert.IsTrue(GatewayEmailQueryCodec.TryParseQuery(arguments, 10, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNull(call!.DeferredError);
        var terms = call.Command.Criteria.Filter!.Terms!;
        Assert.HasCount(Enum.GetValues<MailMessageFilterField>().Length, terms);
        CollectionAssert.AreEquivalent(Enum.GetValues<MailMessageFilterField>(),
            terms.Select(term => term.Field).ToArray());
    }

    [TestMethod]
    public async Task WorkerRejectsExtraTypedMessageQueryFields()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.FindMessages, new JsonObject
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
