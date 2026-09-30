using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapMimeSnapshotBoundaryTests
{
    [TestMethod]
    public void MimeSnapshotPreservesPresentationWithoutTransferringBinaryAttachments()
    {
        var binary = Convert.ToBase64String(new byte[128_000]);
        var raw = Encoding.UTF8.GetBytes("From: Sender <sender@example.test>\r\nSubject: Snapshot\tproof\r\n"
            + "X-Test: folded\r\n\tcontinued\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n"
            + "--b\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello \u00e9\ud83d\ude42\r\n"
            + "--b\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=large.bin\r\n"
            + "Content-Transfer-Encoding: base64\r\n\r\n" + binary + "\r\n--b--\r\n");
        using var message = JmapEmailCodec.Parse(raw);
        var id = Guid.CreateVersion7();
        var stored = new EmailDB
        {
            Id = id,
            FolderId = Guid.CreateVersion7(),
            SizeBytes = raw.Length,
            ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ThreadObjectId = "thread/opaque"
        };
        var properties = JmapEmailCodec.DefaultProperties.Concat(["headers", "bodyStructure", "header:X-Test:asText"]).ToArray();
        var bodyProperties = JmapEmailCodec.DefaultBodyProperties.Concat(["headers", "header:Content-Type"]).ToArray();
        var prior = JmapEmailCodec.BuildEmail(message,
            new(properties, bodyProperties, true, false, false, 10), id, stored);
        var snapshot = JmapEmailCodec.Capture(message, id, raw.Length, true, stored);
        Assert.IsNotNull(snapshot.RootPart);
        var attachment = snapshot.Parts[snapshot.Parts[snapshot.RootPart.Value].Children[1]];
        Assert.AreEqual(128_000L, attachment.DecodedSize);
        Assert.IsNull(attachment.Text);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsLessThan(10_000, json.Length);
        var restored = JsonSerializer.Deserialize<MailMessageSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var rendered = GatewayEmailValueCodec.BuildEmail(restored,
            new(properties, bodyProperties, true, false, false, 10));
        Assert.IsTrue(JsonNode.DeepEquals(prior, rendered), rendered.ToJsonString());
    }

    [TestMethod]
    public void MetadataOnlySnapshotDoesNotDecodeOrTransferText()
    {
        var raw = "Subject: Metadata only\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nPrivate text"u8.ToArray();
        using var message = JmapEmailCodec.Parse(raw);
        var snapshot = JmapEmailCodec.Capture(message, Guid.CreateVersion7(), raw.Length, false);
        Assert.IsNotNull(snapshot.RootPart);
        Assert.IsNull(snapshot.Parts[snapshot.RootPart.Value].Text);
        var response = GatewayEmailValueCodec.BuildEmail(snapshot, new(["subject"], [], false, false, false, 0));
        Assert.AreEqual("Metadata only", response["subject"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "subject" }, response.Select(item => item.Key).ToArray());
    }
}
