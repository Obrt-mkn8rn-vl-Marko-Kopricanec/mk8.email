using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewaySubmissionMutationCodecTests
{
    [TestMethod]
    public void TypedEnvelopeKeepsSizeTextAndRecipientErrorPrecedence()
    {
        var draft = GatewaySubmissionMutationCodec.Draft(JsonNode.Parse("""
            {"identityId":"Iref","emailId":"#draft","envelope":{
                "mailFrom":{"email":"sender@example.test","parameters":{"size":"00010"}},
                "rcptTo":[{"email":"bad address","parameters":{"NOTIFY":"FAILURE"}},
                    {"email":"good@example.test","parameters":{"NOTIFY":"FAILURE"}}]}}
            """)!.AsObject());
        Assert.AreEqual("#draft", draft.MessageReference, StringComparer.Ordinal);
        Assert.IsFalse(draft.InvalidFields);
        Assert.AreEqual("00010", draft.Envelope!.Sender.Parameters!["SIZE"], StringComparer.Ordinal);
        Assert.AreEqual(MailEnvelopeAddressIssue.InvalidAddress, draft.Envelope.Recipients[0].Issue);
        Assert.AreEqual(MailEnvelopeAddressIssue.InvalidParameters, draft.Envelope.Recipients[1].Issue);
        var built = MailSubmissionEnvelopeBuilder.Build(draft.Envelope, new byte[5], "sender@example.test", 20);
        Assert.AreEqual(MailSubmissionMutationError.InvalidProperties, built.Failure!.Error);
        built = MailSubmissionEnvelopeBuilder.Build(draft.Envelope with
        { Recipients = [draft.Envelope.Recipients[0]] }, new byte[5], "sender@example.test", 20);
        Assert.AreEqual(MailSubmissionMutationError.InvalidRecipients, built.Failure!.Error);
        CollectionAssert.AreEqual(ExpectedVector1, built.Failure.InvalidRecipients!.ToArray());
        built = MailSubmissionEnvelopeBuilder.Build(draft.Envelope, new byte[11], "sender@example.test", 20);
        Assert.AreEqual(MailSubmissionMutationError.InvalidProperties, built.Failure!.Error);
    }

    [TestMethod]
    public void DomainDeliveryAssertionsRejectSyntheticReplyObjectsAndDescendantPaths()
    {
        var snapshot = Snapshot();
        var current = GatewaySubmissionGetCodec.Build(snapshot, null);
        var patch = GatewaySubmissionMutationCodec.Patch(current);
        Assert.IsNull(patch.Failure);
        Assert.IsNull(MailSubmissionAssertions.Verify(snapshot, patch));
        var delivery = patch.Assertions.First(item => item.Field == MailSubmissionObservationField.Delivery);
        var canonical = ApplicationValueCodec.Decode(delivery.Changes[0].Value)!;
        Assert.AreEqual("Queued", canonical["to@example.test"]!["Reply"]!["Kind"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.IsNull(canonical["to@example.test"]!["smtpReply"]);
        var bad = GatewaySubmissionMutationCodec.Patch(new JsonObject
        {
            ["deliveryStatus/to@example.test/smtpReply"] = new JsonObject { ["Kind"] = "Queued" },
        });
        Assert.AreEqual(MailSubmissionMutationError.InvalidProperties, MailSubmissionAssertions.Verify(snapshot, bad)!.Error);
        bad = GatewaySubmissionMutationCodec.Patch(new JsonObject
        { ["deliveryStatus/to@example.test/smtpReply/Kind"] = "Queued" });
        Assert.AreEqual(MailSubmissionMutationError.InvalidPatch, MailSubmissionAssertions.Verify(snapshot, bad)!.Error);
    }

    [TestMethod]
    public void UnknownSubmissionFieldsAndBadPathsRetainFailureOrdering()
    {
        var snapshot = Snapshot();
        var patch = GatewaySubmissionMutationCodec.Patch(new JsonObject { ["unknown"] = null });
        Assert.AreEqual(MailSubmissionMutationError.InvalidProperties, MailSubmissionAssertions.Verify(snapshot, patch)!.Error);
        patch = GatewaySubmissionMutationCodec.Patch(new JsonObject { ["unknown/child"] = null });
        Assert.AreEqual(MailSubmissionMutationError.InvalidPatch, MailSubmissionAssertions.Verify(snapshot, patch)!.Error);
        patch = GatewaySubmissionMutationCodec.Patch(new JsonObject { ["undoStatus"] = "canceled", ["unknown"] = true });
        Assert.AreEqual(MailSubmissionMutationError.InvalidProperties, MailSubmissionAssertions.Verify(snapshot, patch)!.Error);
        patch = GatewaySubmissionMutationCodec.Patch(new JsonObject { ["undoStatus"] = "canceled" });
        Assert.AreEqual(MailSubmissionMutationError.CannotUnsend, MailSubmissionAssertions.Verify(snapshot, patch)!.Error);
        patch = GatewaySubmissionMutationCodec.Patch(new JsonObject { ["envelope"] = new JsonObject(), ["envelope/mailFrom"] = null });
        Assert.AreEqual(MailSubmissionMutationError.InvalidPatch, patch.Failure!.Error);
    }

    [TestMethod]
    public void StoredEnvelopeSupportsLegacyCacheAndDomainRoundTripsWithoutWireRequestParsing()
    {
        var row = new JmapEmailSubmissionDB
        {
            EnvelopeSender = "indexed@example.test",
            EnvelopeRecipients = ["indexed-to@example.test"],
            EnvelopeJson = """{"mailFrom":{"email":"legacy@example.test","parameters":{"SIZE":"00012"}},"rcptTo":[]}""",
        };
        var legacy = MailSubmissionEnvelopeCache.Read(row);
        Assert.AreEqual("legacy@example.test", legacy.Sender.Address, StringComparer.Ordinal);
        Assert.AreEqual("00012", legacy.Sender.Parameters!["SIZE"], StringComparer.Ordinal);
        row.EnvelopeJson = MailSubmissionEnvelopeCache.Encode(legacy);
        Assert.AreEqual("legacy@example.test", JsonNode.Parse(row.EnvelopeJson)!["mailFrom"]!["email"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.AreEqual(row.EnvelopeJson, MailSubmissionEnvelopeCache.Encode(MailSubmissionEnvelopeCache.Read(row)), StringComparer.Ordinal);
        row.EnvelopeJson = "{bad";
        var fallback = MailSubmissionEnvelopeCache.Read(row);
        Assert.AreEqual("indexed@example.test", fallback.Sender.Address, StringComparer.Ordinal);
        Assert.AreEqual("indexed-to@example.test", fallback.Recipients[0].Address, StringComparer.Ordinal);
        row.EnvelopeJson = """{"mailFrom":{"email":null,"parameters":null},"rcptTo":[]}""";
        Assert.AreEqual("indexed@example.test", MailSubmissionEnvelopeCache.Read(row).Sender.Address, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerRejectsLegacyDraftAndUndefinedSubmissionAssertionBeforeMutation()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var arguments = new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["create"] = new JsonObject { ["draft"] = new JsonObject { ["emailId"] = "#email", ["identityId"] = "#identity" } },
            ["update"] = new JsonObject { ["S11111111111111111111111111111111"] = new JsonObject { ["undoStatus"] = "final" } },
        };
        Assert.IsTrue(GatewaySubmissionSetCodec.TryParse(arguments, 2, out var call, out _));
        var encoded = JsonSerializer.SerializeToNode(call!.Command, SerializationOptions2)!.AsObject();
        var original = encoded["creates"]![0]!["draft"]!.DeepClone();
        encoded["creates"]![0]!["draft"] = JsonSerializer.SerializeToNode(ApplicationValueCodec.Encode(new JsonObject()));
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Submission],
            MailOperationKind.MutateSubmissions, encoded, new Dictionary<string, string>(StringComparer.Ordinal));
        var error = await Assert.ThrowsAsync<MailApplicationException>(() => processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, error.Failure.Kind);
        encoded["creates"]![0]!["draft"] = original;
        encoded["updates"]![0]!["patch"]!["assertions"]![0]!["field"] = 999;
        error = await Assert.ThrowsAsync<MailApplicationException>(() => processor.ExecuteAsync(command, fixture.User, null)).ConfigureAwait(false);
        Assert.AreEqual(MailFailureKind.MalformedBatch, error.Failure.Kind);
    }

    private static MailSubmissionSnapshot Snapshot() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Iidentity", "Eemail", "Tthread", new(new("sender@example.test", null), [new("to@example.test", null)]),
        "sender@example.test", ["to@example.test"], new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), "final",
        [new("to@example.test", MailSubmissionDeliveryState.Pending, null)]);
    private static readonly string[] ExpectedVector1 = new[] { "bad address" };
    private static readonly JsonSerializerOptions SerializationOptions2 = new JsonSerializerOptions(JsonSerializerDefaults.Web);
}
