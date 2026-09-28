using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class ApplicationValueCodecTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 256 };

    [TestMethod]
    [DataRow(0xd800)]
    [DataRow(0xdfff)]
    [DataRow(0xfdd0)]
    [DataRow(0xffff)]
    [DataRow(0xfffd)]
    [DataRow(0x1f600)]
    public void TransportPreservesRawCharactersUntilGatewayNormalization(int codePoint)
    {
        var text = codePoint <= char.MaxValue ? new string((char)codePoint, 1) : char.ConvertFromUtf32(codePoint);
        var source = new JsonObject { [text] = text, ["array"] = new JsonArray(text, null, true, 1.25) };
        var restored = RoundTrip(source);
        Assert.AreEqual(text, restored.First().Key);
        Assert.AreEqual(text, restored[text]!.GetValue<string>());
        Assert.AreEqual(text, restored["array"]![0]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(GatewayJmapJson.SanitizeResponse(source), GatewayJmapJson.SanitizeResponse(restored)));
    }

    [TestMethod]
    public void TransportRetainsDistinctKeysThatWouldCollideDuringJsonEncoding()
    {
        var source = new JsonObject
        {
            ["\ud800"] = "first",
            ["\udfff"] = "second",
            ["\ufffd"] = "third",
            ["x"] = 1,
            ["X"] = 2
        };
        var restored = RoundTrip(source);
        CollectionAssert.AreEqual(source.Select(member => member.Key).ToArray(), restored.Select(member => member.Key).ToArray());
        var display = GatewayJmapJson.SanitizeResponse(restored);
        Assert.AreEqual("first", display["\ufffd"]!.GetValue<string>());
        Assert.AreEqual("second", display["\ufffd~2"]!.GetValue<string>());
        Assert.AreEqual("third", display["\ufffd~3"]!.GetValue<string>());
        Assert.AreEqual(1, display["x"]!.GetValue<int>());
        Assert.AreEqual(2, display["X"]!.GetValue<int>());
    }

    [TestMethod]
    public void NumbersDatesCharactersAndEmptyShapesRoundTrip()
    {
        var source = new JsonObject
        {
            ["date"] = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
            ["char"] = '\ud800',
            ["empty"] = "",
            ["object"] = new JsonObject(),
            ["array"] = new JsonArray(),
            ["number"] = JsonNode.Parse("1e-300"),
            ["bool"] = false,
            ["null"] = null
        };
        var result = RoundTrip(source);
        Assert.AreEqual("2026-09-27T00:00:00+00:00", result["date"]!.GetValue<string>());
        Assert.AreEqual("\ud800", result["char"]!.GetValue<string>());
        Assert.AreEqual("1e-300", result["number"]!.ToJsonString());
        Assert.IsFalse(result["bool"]!.GetValue<bool>());
        Assert.IsNull(result["null"]);
        Assert.AreEqual(0, result["object"]!.AsObject().Count);
        Assert.AreEqual(0, result["array"]!.AsArray().Count);
    }

    [TestMethod]
    [DataRow(ApplicationValueKind.None)]
    [DataRow((ApplicationValueKind)999)]
    [DataRow(ApplicationValueKind.Text)]
    [DataRow(ApplicationValueKind.Number)]
    [DataRow(ApplicationValueKind.Boolean)]
    [DataRow(ApplicationValueKind.Map)]
    [DataRow(ApplicationValueKind.Array)]
    public void MissingOrUndefinedValueShapeFailsClosed(ApplicationValueKind kind) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Decode(new(kind)));

    [TestMethod]
    public void CorruptCodeUnitsOrDuplicateKeysFailClosed()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Decode(new(ApplicationValueKind.Text, CodeUnits: new byte[] { 1 })));
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Decode(new(ApplicationValueKind.Null, Boolean: false)));
        Assert.ThrowsExactly<ArgumentException>(() => ApplicationValueCodec.Decode(new(ApplicationValueKind.Map,
            Members: [new(new byte[] { 65, 0 }, new(ApplicationValueKind.Null)), new(new byte[] { 65, 0 }, new(ApplicationValueKind.Null))])));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<ApplicationValue>("{}", Options));
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Decode(
            new(ApplicationValueKind.Text, CodeUnits: ReadOnlyMemory<byte>.Empty, Text: "")));
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Decode(
            new(ApplicationValueKind.Text, Text: "\ud800")));
    }

    [TestMethod]
    public void AMaximumDefaultMailBodyDoesNotInflatePastTheTransportLimit()
    {
        var source = new JsonObject { ["body"] = new string('a', 25 * 1024 * 1024) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ApplicationValueCodec.Encode(source), Options);
        Assert.IsTrue(bytes.Length < 26 * 1024 * 1024);
        var restored = ApplicationValueCodec.Decode(JsonSerializer.Deserialize<ApplicationValue>(bytes, Options)!)!;
        Assert.AreEqual(source["body"]!.GetValue<string>(), restored["body"]!.GetValue<string>());
    }

    [TestMethod]
    public void MaximumValueDepthSurvivesEnvelopeAndExcessDepthIsRejected()
    {
        JsonObject root = new();
        var current = root;
        for (var index = 0; index < 63; index++)
        {
            var child = new JsonObject();
            current["next"] = child;
            current = child;
        }
        current["leaf"] = "text";
        var result = RoundTrip(root);
        Assert.IsNotNull(result["next"]);
        current["tooDeep"] = new JsonArray(new JsonArray(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Encode(root));
        var encoded = new ApplicationValue(ApplicationValueKind.Null);
        for (var index = 0; index < 65; index++)
            encoded = new(ApplicationValueKind.Array, Items: [encoded]);
        Assert.ThrowsExactly<InvalidOperationException>(() => ApplicationValueCodec.Decode(encoded));
    }

    private static JsonObject RoundTrip(JsonObject source) => (JsonObject)ApplicationValueCodec.Decode(
        JsonSerializer.Deserialize<ApplicationValue>(JsonSerializer.Serialize(ApplicationValueCodec.Encode(source), Options), Options)!)!;
}
