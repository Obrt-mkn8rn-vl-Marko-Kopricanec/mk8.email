using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayMailboxSetCodecTests
{
    [TestMethod]
    public void GatewayOwnsMailboxSetArgumentsAndCreatedResponseSelection()
    {
        var arguments = JsonNode.Parse("""
        {
          "accountId":"A11111111111111111111111111111111",
          "create": {
            "explicit": {"name":"Explicit", "parentId":null, "role":null,
              "sortOrder":5, "isSubscribed":false},
            "normalized": {"name":"Cafe\u0301"}
          },
          "update": {"M22222222222222222222222222222222":{"sortOrder":7}},
          "destroy": ["M33333333333333333333333333333333"]
        }
        """)!.AsObject();
        Assert.IsTrue(GatewayMailboxSetCodec.TryParse(arguments, 4, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(2, call.Command.Creates.Count);
        Assert.AreEqual(1, call.Command.Updates.Count);
        Assert.AreEqual(1, call.Command.Destroys.Count);
        Assert.AreEqual("Explicit", call.Command.Creates[0].Values!.Name, StringComparer.Ordinal);
        Assert.AreEqual(MailFolderFields.SortOrder, call.Command.Updates[0].Patch.Fields);
        Assert.AreEqual(7L, call.Command.Updates[0].Patch.Values!.SortOrder);
        var explicitId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var normalizedId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var result = new MailFolderMutationResult(MailFolderMutationStatus.Ok, "s1", "s2",
        [
            new("explicit", new(explicitId, "Explicit", null, null, 5, false), null),
            new("normalized", new(normalizedId, "Caf\u00e9", null, null, 0, true), null),
        ],
        [new("M22222222222222222222222222222222",
            Guid.Parse("22222222-2222-2222-2222-222222222222"), null)],
        [new("M33333333333333333333333333333333",
            Guid.Parse("33333333-3333-3333-3333-333333333333"), null)]);
        var rendered = GatewayMailboxSetCodec.Render(call, result);
        Assert.AreEqual(MailOperationKind.MutateFolders, rendered.Operation);
        var explicitResponse = rendered.Data["created"]!["explicit"]!.AsObject();
        CollectionAssert.AreEquivalent(
            ExpectedVector1,
            explicitResponse.Select(item => item.Key).ToArray());
        var normalizedResponse = rendered.Data["created"]!["normalized"]!.AsObject();
        Assert.AreEqual("Caf\u00e9", normalizedResponse["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(0L, normalizedResponse["sortOrder"]!.GetValue<long>());
        Assert.AreEqual("M33333333333333333333333333333333",
            rendered.Data["destroyed"]![0]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void GatewayDecodesFolderPatchPathsIntoValuesAndImmutableExpectations()
    {
        var arguments = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111","update":{
          "M22222222222222222222222222222222": {
            "parentId":"#parent", "sortOrder":null, "isSubscribed":null,
            "totalEmails":12, "myRights/mayRename":false
          },
          "absent":{"name/nested":"invalid"},
          "unknown":{"unregistered":null}
        }}
        """)!.AsObject();
        Assert.IsTrue(GatewayMailboxSetCodec.TryParse(arguments, 3, out var call, out _));
        Assert.IsNotNull(call);
        var patch = call.Command.Updates[0].Patch;
        Assert.AreEqual(MailFolderFields.Parent | MailFolderFields.SortOrder | MailFolderFields.Subscription, patch.Fields);
        Assert.AreEqual("#parent", patch.Values!.ParentReference, StringComparer.Ordinal);
        Assert.AreEqual(0L, patch.Values.SortOrder);
        Assert.IsTrue(patch.Values.IsSubscribed);
        Assert.AreEqual(2, patch.Expectations.Count);
        Assert.AreEqual(MailFolderInvariant.TotalMessages, patch.Expectations[0].Field);
        Assert.AreEqual(12L, patch.Expectations[0].Count);
        Assert.IsTrue(patch.Expectations[1].MatchesProtected);
        Assert.IsFalse(patch.Expectations[1].MatchesOrdinary);
        Assert.AreEqual(MailFolderMutationError.InvalidPatch, call.Command.Updates[1].Patch.Failure!.Error);
        CollectionAssert.AreEqual(ExpectedVector2, call.Command.Updates[2].Patch.Failure!.Properties!.ToArray());
    }

    [TestMethod]
    public void InvalidFolderValuesRemainPerItemFailuresInsteadOfWorkerWireDocuments()
    {
        var arguments = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111","create":{
          "invalid":{"name":"Valid", "isSubscribed":null},
          "immutable":{"name":"Valid", "totalEmails":0},
          "child":{"name":"Child", "parentId":"#parent"}
        }}
        """)!.AsObject();
        Assert.IsTrue(GatewayMailboxSetCodec.TryParse(arguments, 3, out var call, out _));
        Assert.IsNotNull(call);
        Assert.IsNull(call.Command.Creates[0].Values);
        Assert.AreEqual(MailFolderMutationError.InvalidProperties, call.Command.Creates[0].Failure!.Error);
        CollectionAssert.AreEqual(ExpectedVector3, call.Command.Creates[1].Failure!.Properties!.ToArray());
        Assert.AreEqual("#parent", call.Command.Creates[2].Values!.ParentReference, StringComparer.Ordinal);
    }

    [TestMethod]
    public void GatewayRejectsMalformedMailboxSetEnvelopeAndIncompleteWorkerResult()
    {
        var malformed = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111",
         "onDestroyRemoveEmails":null}
        """)!.AsObject();
        Assert.IsFalse(GatewayMailboxSetCodec.TryParse(malformed, 3, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
        var valid = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111",
         "create":{"one":{"name":"One"}}}
        """)!.AsObject();
        Assert.IsTrue(GatewayMailboxSetCodec.TryParse(valid, 1, out var call, out _));
        Assert.IsNotNull(call);
        Assert.Throws<InvalidOperationException>(() => GatewayMailboxSetCodec.Render(call,
            new(MailFolderMutationStatus.Ok, "s1", "s2", [], [], [])));
        Assert.IsFalse(GatewayMailboxSetCodec.TryParse(valid, 0, out _, out failure));
        Assert.AreEqual("requestTooLarge", failure, StringComparer.Ordinal);
    }
    private static readonly string[] ExpectedVector1 = new[] { "id", "totalEmails", "unreadEmails", "totalThreads", "unreadThreads", "myRights" };
    private static readonly string[] ExpectedVector2 = new[] { "unregistered" };
    private static readonly string[] ExpectedVector3 = new[] { "totalEmails" };
}
