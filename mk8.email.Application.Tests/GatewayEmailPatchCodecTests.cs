using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayEmailPatchCodecTests
{
    [TestMethod]
    public void GatewayProducesCanonicalDomainAssertionsAndTypedFlagChanges()
    {
        var parsed = GatewayEmailPatchCodec.Parse(JsonNode.Parse("""
            {"keywords/$seen":true,"subject":"Proof","bodyStructure":{
              "type":"text/plain","partId":"1",
              "header:X-Author:asAddresses":[{"name":"Sender","email":"sender@example.test"}]},
             "bodyValues":{"1":{"value":"Text","isEncodingProblem":false,"isTruncated":false}}}
            """)!.AsObject());
        Assert.IsNull(parsed.Failure);
        Assert.IsTrue(parsed.RequiresMime);
        Assert.HasCount(1, parsed.Flags);
        Assert.AreEqual(MailMessageFlagField.Keywords, parsed.Flags[0].Field);
        Assert.AreEqual(MailMessageFlagChangeKind.Set, parsed.Flags[0].Kind);
        Assert.AreEqual("$seen", parsed.Flags[0].Entries[0].Key);
        var body = parsed.Assertions.First(item => item.Field == MailMessageObservationField.BodyTree);
        var expected = ApplicationValueCodec.Decode(body.Changes[0].Value)!;
        Assert.AreEqual("text/plain", expected["MediaType"]!.GetValue<string>());
        Assert.AreEqual("1", expected["Path"]!.GetValue<string>());
        Assert.IsNull(expected["type"]);
        var custom = parsed.PartFields.First(item => item.Field == MailMimePartField.Header);
        Assert.AreEqual("sender@example.test", expected[custom.Key]![0]!["Address"]!.GetValue<string>());
        Assert.AreEqual("Sender", expected[custom.Key]![0]!["DisplayName"]!.GetValue<string>());
        var text = parsed.Assertions.First(item => item.Field == MailMessageObservationField.TextValues);
        Assert.IsTrue(text.MatchVisibleTextValues);
        Assert.AreEqual("Text", ApplicationValueCodec.Decode(text.Changes[0].Value)!["1"]!["Text"]!.GetValue<string>());
    }

    [TestMethod]
    public void ImplicitPatchMergePreservesLastWriteAndCrossFragmentPathConflicts()
    {
        var first = GatewayEmailPatchCodec.ParseFragments(new JsonObject { ["keywords/$seen"] = false });
        var last = GatewayEmailPatchCodec.ParseFragments(new JsonObject { ["keywords/$seen"] = true });
        var combined = MailMessagePatchMerger.Merge(first.Concat(last).ToArray());
        Assert.IsNull(combined.Failure);
        Assert.HasCount(1, combined.Flags);
        Assert.AreEqual(MailMessageFlagValue.Enabled, combined.Flags[0].Entries[0].Value);
        var whole = GatewayEmailPatchCodec.ParseFragments(new JsonObject { ["keywords"] = new JsonObject() });
        combined = MailMessagePatchMerger.Merge(first.Concat(whole).ToArray());
        Assert.AreEqual(MailMessageMutationError.InvalidPatch, combined.Failure!.Error);
    }

    [TestMethod]
    public void ImplicitPatchMergeDropsOverwrittenBodyFailuresAndKeepsUnknownFieldPrecedence()
    {
        var invalid = GatewayEmailPatchCodec.ParseFragments(new JsonObject
        { ["bodyStructure"] = new JsonObject { ["unknown"] = true } });
        var corrected = GatewayEmailPatchCodec.ParseFragments(new JsonObject
        { ["bodyStructure"] = new JsonObject { ["type"] = "text/plain" } });
        var combined = MailMessagePatchMerger.Merge(invalid.Concat(corrected).ToArray());
        Assert.IsNull(combined.Failure);
        Assert.HasCount(1, combined.PartFields);
        Assert.AreEqual(MailMimePartField.MediaType, combined.PartFields[0].Field);
        var conflict = GatewayEmailPatchCodec.ParseFragments(new JsonObject
        { ["keywords"] = new JsonObject(), ["keywords/$seen"] = true, ["unregistered"] = true });
        combined = MailMessagePatchMerger.Merge(conflict);
        Assert.AreEqual(MailMessageMutationError.InvalidProperties, combined.Failure!.Error);
        CollectionAssert.AreEqual(new[] { "unregistered" }, combined.Failure.Properties!.ToArray());
    }

    [TestMethod]
    public void GatewayRejectsNumericHeaderFormsAndConflictingPatchPaths()
    {
        Assert.IsFalse(GatewayEmailValueCodec.TryHeaderObservation("header:Subject:as0", out _));
        var parsed = GatewayEmailPatchCodec.Parse(new JsonObject
        { ["keywords"] = new JsonObject(), ["keywords/$seen"] = true });
        Assert.AreEqual(MailMessageMutationError.InvalidPatch, parsed.Failure!.Error);
        parsed = GatewayEmailPatchCodec.Parse(new JsonObject { ["subject/no-child"] = "value" });
        Assert.IsNull(parsed.Failure);
        Assert.IsTrue(parsed.RequiresMime);
    }

    [TestMethod]
    public async Task WorkerRejectsLegacyAndUndefinedTypedPatchFieldsBeforeMutation()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var arguments = new JsonObject
        {
            ["accountId"] = fixture.AccountId,
            ["update"] = new JsonObject
            {
                ["E11111111111111111111111111111111"] =
                new JsonObject { ["keywords/$seen"] = true }
            },
        };
        Assert.IsTrue(GatewayEmailSetCodec.TryParse(arguments, 2, out var call, out _));
        var encoded = JsonSerializer.SerializeToNode(call!.Command,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var original = encoded["updates"]![0]!["patch"]!.DeepClone();
        encoded["updates"]![0]!["patch"] = JsonSerializer.SerializeToNode(
            ApplicationValueCodec.Encode(new JsonObject { ["keywords/$seen"] = true }));
        var command = new MailOperationCommand([MailFeature.Basic, MailFeature.Messages],
            MailOperationKind.MutateMessages, encoded, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
        encoded["updates"]![0]!["patch"] = original;
        original["flags"]![0]!["field"] = 999;
        failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(command, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
