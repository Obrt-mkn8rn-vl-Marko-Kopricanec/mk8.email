using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayMailboxSetCodecTests
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
            new[] { "id", "totalEmails", "unreadEmails", "totalThreads", "unreadThreads", "myRights" },
            explicitResponse.Select(item => item.Key).ToArray());
        var normalizedResponse = rendered.Data["created"]!["normalized"]!.AsObject();
        Assert.AreEqual("Caf\u00e9", normalizedResponse["name"]!.GetValue<string>());
        Assert.AreEqual(0L, normalizedResponse["sortOrder"]!.GetValue<long>());
        Assert.AreEqual("M33333333333333333333333333333333",
            rendered.Data["destroyed"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayRejectsMalformedMailboxSetEnvelopeAndIncompleteWorkerResult()
    {
        var malformed = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111",
         "onDestroyRemoveEmails":null}
        """)!.AsObject();
        Assert.IsFalse(GatewayMailboxSetCodec.TryParse(malformed, 3, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
        var valid = JsonNode.Parse("""
        {"accountId":"A11111111111111111111111111111111",
         "create":{"one":{"name":"One"}}}
        """)!.AsObject();
        Assert.IsTrue(GatewayMailboxSetCodec.TryParse(valid, 1, out var call, out _));
        Assert.IsNotNull(call);
        Assert.Throws<InvalidOperationException>(() => GatewayMailboxSetCodec.Render(call,
            new(MailFolderMutationStatus.Ok, "s1", "s2", [], [], [])));
        Assert.IsFalse(GatewayMailboxSetCodec.TryParse(valid, 0, out _, out failure));
        Assert.AreEqual("requestTooLarge", failure);
    }
}
