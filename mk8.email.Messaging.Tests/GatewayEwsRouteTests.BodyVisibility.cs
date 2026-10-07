using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow("mixed", "visible", "Best")]
    [DataRow("mixed", "visible", "Text")]
    [DataRow("mixed", "visible", "HTML")]
    [DataRow("alternative", "visible", "Best")]
    [DataRow("alternative", "visible", "Text")]
    [DataRow("alternative", "visible", "HTML")]
    [DataRow("mixed", "visible-alternative", "Best")]
    [DataRow("mixed", "visible-alternative", "Text")]
    [DataRow("mixed", "visible-alternative", "HTML")]
    [DataRow("alternative", "visible-alternative", "Best")]
    [DataRow("alternative", "visible-alternative", "Text")]
    [DataRow("alternative", "visible-alternative", "HTML")]
    [DataRow("mixed", "nested-only", "Best")]
    [DataRow("mixed", "nested-only", "Text")]
    [DataRow("mixed", "nested-only", "HTML")]
    [DataRow("alternative", "nested-only", "Best")]
    [DataRow("alternative", "nested-only", "Text")]
    [DataRow("alternative", "nested-only", "HTML")]
    [DataRow("mixed", "root-only", "Best")]
    [DataRow("mixed", "root-only", "Text")]
    [DataRow("mixed", "root-only", "HTML")]
    [DataRow("alternative", "root-only", "Best")]
    [DataRow("alternative", "root-only", "Text")]
    [DataRow("alternative", "root-only", "HTML")]
    public async Task ActualWorkerMultipartAttachmentAncestryIsExcludedBeforeBodySelection(string subtype, string layout, string requested)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, AttachedBodyMime(subtype, layout)).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await BodyResponseAsync(fixture, item, requested).ConfigureAwait(false);
        var body = XDocument.Parse(xml).Descendants(Types + "Body").Single();
        var html = requested is "HTML" || requested is "Best" && layout is "visible-alternative";
        var expected = layout switch
        {
            "visible" => html ? "<pre>Visible body</pre>" : "Visible body",
            "visible-alternative" => html ? "<p>Visible HTML</p>" : "Visible plain",
            _ => "",
        };
        Assert.AreEqual(html ? "HTML" : "Text", (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual(expected, body.Value, StringComparer.Ordinal);
        Assert.AreEqual("false", (string?)body.Attribute("IsTruncated"), StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("PRIVATE", StringComparison.Ordinal));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var stored = await database.Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false);
        Assert.IsNull(stored.RawMessage);
        Assert.AreEqual("azure-blob", stored.RawMessageObjectProvider, StringComparer.Ordinal);
    }

    private static string AttachedBodyMime(string subtype, string layout)
    {
        var attached = $"Content-Type: multipart/{subtype}; boundary=private\r\nContent-Disposition: AtTaChMeNt\r\n\r\n"
            + "--private\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: inline\r\n\r\nPRIVATE ATTACHMENT PLAIN\r\n"
            + "--private\r\nContent-Type: text/html; charset=utf-8\r\nContent-Disposition: inline\r\n\r\n<p>PRIVATE ATTACHMENT HTML</p>\r\n--private--\r\n";
        if (layout is "root-only") return BodyHeaders + attached;
        if (layout is "nested-only")
            attached = "Content-Type: multipart/related; boundary=nested\r\nContent-Disposition: inline\r\n\r\n--nested\r\n" + attached + "--nested--\r\n";
        var visible = layout switch
        {
            "visible" => "--outside\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nVisible body\r\n",
            "visible-alternative" => "--outside\r\nContent-Type: multipart/alternative; boundary=visible\r\n\r\n"
                + "--visible\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nVisible plain\r\n"
                + "--visible\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Visible HTML</p>\r\n--visible--\r\n",
            _ => "",
        };
        return BodyHeaders + "Content-Type: multipart/mixed; boundary=outside\r\n\r\n" + visible + "--outside\r\n" + attached + "--outside--\r\n";
    }
}
