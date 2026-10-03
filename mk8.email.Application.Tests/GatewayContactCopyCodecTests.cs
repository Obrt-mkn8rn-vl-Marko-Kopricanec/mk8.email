using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayContactCopyCodecTests
{
    private const string SourceId = "Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TargetId = "Abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [TestMethod]
    public void GatewayParsesTypedContactCopyAndRendersPerItemFailure()
    {
        var arguments = new JsonObject
        {
            ["fromAccountId"] = SourceId,
            ["accountId"] = TargetId,
            ["ifFromInState"] = "s1",
            ["ifInState"] = "s2",
            ["create"] = new JsonObject
            {
                ["first"] = new JsonObject { ["id"] = "C1" },
                ["second"] = new JsonObject()
            },
            ["onSuccessDestroyOriginal"] = true,
            ["destroyFromIfInState"] = "s3",
        };
        Assert.IsTrue(GatewayContactCopyCodec.TryParse(arguments, 2, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), call.Command.SourceAccountId);
        Assert.IsTrue(call.Command.SourceReferenceParseable);
        Assert.IsTrue(call.Command.SourceReferenceEligible);
        Assert.AreEqual(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), call.Command.TargetAccountId);
        Assert.IsTrue(call.Command.TargetReferenceEligible);
        CollectionAssert.AreEqual(ExpectedVector1, call.Command.CreationIds.ToArray());
        var rendered = GatewayContactCopyCodec.Render(call,
            new MailContactCopyResult(MailContactCopyStatus.Ok, "s2"));
        Assert.AreEqual(MailOperationKind.CopyContacts, rendered.Operation);
        Assert.AreEqual("s2", rendered.Data["oldState"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("s2", rendered.Data["newState"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNull(rendered.Data["created"]);
        Assert.AreEqual("forbidden", rendered.Data["notCreated"]!["first"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual("forbidden", rendered.Data["notCreated"]!["second"]!["type"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    [TestMethod]
    public void GatewayRetainsParseableButNoncanonicalAccountReferencesForWorkerPrecedence()
    {
        var arguments = new JsonObject
        {
            ["fromAccountId"] = SourceId.ToUpperInvariant(),
            ["accountId"] = TargetId,
            ["create"] = new JsonObject(),
        };
        Assert.IsTrue(GatewayContactCopyCodec.TryParse(arguments, 2, out var call, out _));
        Assert.IsTrue(call!.Command.SourceReferenceParseable);
        Assert.IsFalse(call.Command.SourceReferenceEligible);
        var unsupported = GatewayContactCopyCodec.Render(call,
            new MailContactCopyResult(MailContactCopyStatus.FromAccountNotSupported, null));
        Assert.AreEqual("fromAccountNotSupportedByMethod", unsupported.Data["type"]!.GetValue<string>(), StringComparer.Ordinal);
        arguments["fromAccountId"] = "invalid";
        Assert.IsTrue(GatewayContactCopyCodec.TryParse(arguments, 2, out call, out _));
        Assert.IsFalse(call!.Command.SourceReferenceParseable);
        Assert.AreEqual(Guid.Empty, call.Command.SourceAccountId);
    }

    [TestMethod]
    public void GatewayRejectsMalformedOrOversizedCopyBeforeDispatch()
    {
        var arguments = new JsonObject
        {
            ["fromAccountId"] = SourceId,
            ["accountId"] = TargetId,
            ["create"] = new JsonObject { ["one"] = new JsonObject(), ["two"] = new JsonObject() },
        };
        Assert.IsFalse(GatewayContactCopyCodec.TryParse(arguments, 1, out _, out var failure));
        Assert.AreEqual("requestTooLarge", failure, StringComparer.Ordinal);
        arguments["onSuccessDestroyOriginal"] = null;
        Assert.IsFalse(GatewayContactCopyCodec.TryParse(arguments, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
        arguments["onSuccessDestroyOriginal"] = false;
        arguments["accountId"] = SourceId;
        Assert.IsFalse(GatewayContactCopyCodec.TryParse(arguments, 2, out _, out failure));
        Assert.AreEqual("invalidArguments", failure, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerRejectsExtraTypedContactCopyFields()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Contacts],
            MailOperationKind.CopyContacts, new JsonObject
            {
                ["sourceAccountId"] = fixture.InboxId.ToString(),
                ["sourceReferenceParseable"] = true,
                ["sourceReferenceEligible"] = true,
                ["targetAccountId"] = Guid.CreateVersion7().ToString(),
                ["targetReferenceParseable"] = true,
                ["targetReferenceEligible"] = true,
                ["ifFromInState"] = null,
                ["ifInState"] = null,
                ["creationIds"] = new JsonArray(),
                ["extra"] = true,
            }, new Dictionary<string, string>(StringComparer.Ordinal));
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
    private static readonly string[] ExpectedVector1 = new[] { "first", "second" };
}
