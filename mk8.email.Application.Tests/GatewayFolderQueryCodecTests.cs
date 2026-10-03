using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayFolderQueryCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";
    private const string FolderId = "M22222222222222222222222222222222";

    [TestMethod]
    public void GatewayParsesTypedFolderQueryAndRendersTreeWindow()
    {
        var arguments = JsonNode.Parse($$"""
        {"accountId":"{{AccountId}}","filter":{"operator":"AND","conditions":[
          {"parentId":"{{FolderId}}","role":null,"hasAnyRole":false},null]},
         "sort":[{"property":"name","isAscending":false,"collation":"i;ascii-numeric"}],
         "sortAsTree":true,"filterAsTree":true,"anchor":"{{FolderId}}",
         "position":"ignored","anchorOffset":1,"limit":100,"calculateTotal":true}
        """)!.AsObject();
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseQuery(arguments, 2, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual(Guid.Parse("22222222-2222-2222-2222-222222222222"), call.Command.AnchorId);
        Assert.IsTrue(call.Command.AnchorCanMatch);
        Assert.AreEqual(2, call.Command.Limit);
        Assert.IsTrue(call.Command.Criteria.SortAsTree);
        Assert.IsTrue(call.Command.Criteria.FilterAsTree);
        Assert.AreEqual(MailFolderFilterOperator.And, call.Command.Criteria.Filter!.Operator);
        Assert.HasCount(2, call.Command.Criteria.Filter.Conditions!);
        var child = call.Command.Criteria.Filter.Conditions![0];
        Assert.AreEqual(MailFolderParentConstraint.Folder, child.ParentConstraint);
        Assert.AreEqual(Guid.Parse("22222222-2222-2222-2222-222222222222"), child.ParentId);
        Assert.IsTrue(child.MatchNullRole);
        Assert.AreEqual(MailStringCollation.AsciiNumeric, call.Command.Criteria.Sort[0].Collation);
        var response = GatewayFolderQueryCodec.RenderQuery(call,
            new MailFolderQueryResult(MailFolderQueryStatus.Ok, "s2", 1,
                [Guid.Parse("22222222-2222-2222-2222-222222222222")], 7));
        Assert.AreEqual(MailOperationKind.FindFolders, response.Operation);
        Assert.IsFalse(response.Data["canCalculateChanges"]!.GetValue<bool>());
        Assert.AreEqual(7, response.Data["total"]!.GetValue<int>());
        Assert.AreEqual(2, response.Data["limit"]!.GetValue<int>());
        Assert.AreEqual(FolderId, response.Data["ids"]![0]!.GetValue<string>(), StringComparer.Ordinal);
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
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseQuery(arguments, 2, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        Assert.IsTrue(call.Command.CheckAccountOnly);
        Assert.AreEqual("unsupportedFilter", call.DeferredError, StringComparer.Ordinal);
        var denied = GatewayFolderQueryCodec.RenderQuery(call,
            new MailFolderQueryResult(MailFolderQueryStatus.AccountNotFound, null, 0, [], 0));
        Assert.AreEqual("accountNotFound", denied.Data["type"]!.GetValue<string>(), StringComparer.Ordinal);
        var authorized = GatewayFolderQueryCodec.RenderQuery(call,
            new MailFolderQueryResult(MailFolderQueryStatus.Authorized, null, 0, [], 0));
        Assert.AreEqual("unsupportedFilter", authorized.Data["type"]!.GetValue<string>(), StringComparer.Ordinal);
        arguments["filter"] = new JsonObject();
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseQuery(arguments, 2, out call, out _));
        Assert.AreEqual("unsupportedSort", call!.DeferredError, StringComparer.Ordinal);
        arguments["sort"] = new JsonArray(new JsonObject { ["property"] = "name", ["collation"] = null });
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseQuery(arguments, 2, out call, out _));
        Assert.AreEqual("invalidArguments", call!.DeferredError, StringComparer.Ordinal);
    }

    [TestMethod]
    public void GatewayHandlesUnmatchableParentAndLegacyEmptyGuidAnchorSeparately()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["filter"] = new JsonObject { ["parentId"] = "opaque" },
            ["anchor"] = "M00000000000000000000000000000000",
        };
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseQuery(arguments, 10, out var canonical, out _));
        Assert.AreEqual(MailFolderParentConstraint.Impossible, canonical!.Command.Criteria.Filter!.ParentConstraint);
        Assert.AreEqual(Guid.Empty, canonical.Command.AnchorId);
        Assert.IsTrue(canonical.Command.AnchorCanMatch);
        arguments["anchor"] = "opaque";
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseQuery(arguments, 10, out var unmatchable, out _));
        Assert.AreEqual(Guid.Empty, unmatchable!.Command.AnchorId);
        Assert.IsFalse(unmatchable.Command.AnchorCanMatch);
    }

    [TestMethod]
    public void GatewayParsesAndRendersFolderQueryChanges()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["sinceQueryState"] = "s1",
            ["maxChanges"] = 2,
            ["upToId"] = FolderId,
            ["calculateTotal"] = true,
        };
        Assert.IsTrue(GatewayFolderQueryCodec.TryParseChanges(arguments, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.AreEqual(2L, call!.Command.MaxChanges);
        Assert.AreEqual(MailFolderSortField.SortOrder, call.Command.Criteria.Sort[0].Field);
        var rendered = GatewayFolderQueryCodec.RenderChanges(call,
            new MailFolderQueryChangesResult(MailFolderQueryStatus.Ok, "s2", [FolderId],
                [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 4)], 8));
        Assert.AreEqual(MailOperationKind.FindFolderChanges, rendered.Operation);
        Assert.AreEqual("s1", rendered.Data["oldQueryState"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(FolderId, rendered.Data["removed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("M33333333333333333333333333333333", rendered.Data["added"]![0]!["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(4, rendered.Data["added"]![0]!["index"]!.GetValue<int>());
        Assert.AreEqual(8, rendered.Data["total"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task WorkerRejectsExtraTypedFolderQueryFields()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.FindFolders, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["criteria"] = new JsonObject { ["sort"] = new JsonArray() },
                ["checkAccountOnly"] = false,
                ["position"] = 0,
                ["anchorId"] = null,
                ["anchorCanMatch"] = false,
                ["anchorOffset"] = 0,
                ["limit"] = 10,
                ["extra"] = true,
            }, new Dictionary<string, string>(StringComparer.Ordinal));
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
