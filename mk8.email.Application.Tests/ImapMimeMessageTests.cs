using mk8.email.Imap.Presentation.Protocol;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapMimeMessageTests
{
    [TestMethod]
    public void BodyStructureIncludesMimeExtensionsWhileBodyRemainsBasic()
    {
        const string message =
            "From: sender@example.net\r\n" +
            "To: recipient@example.net\r\n" +
            "Subject: MIME structure\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"mix\"\r\n\r\n" +
            "--mix\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            "hello\r\n" +
            "--mix\r\n" +
            "Content-Type: image/png\r\n" +
            "Content-Disposition: inline; filename=\"pixel.png\"\r\n" +
            "Content-ID: <pixel@example.net>\r\n" +
            "Content-MD5: dGVzdC1kaWdlc3Q=\r\n" +
            "Content-Language: en, fr\r\n" +
            "Content-Location: images/pixel.png\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "iVBORw0KGgo=\r\n" +
            "--mix--\r\n";

        using var mimeMessage = ImapMimeMessage.TryParse(message);

        Assert.IsNotNull(mimeMessage);
        StringAssert.Contains(mimeMessage.BodyStructure, "(\"BOUNDARY\" \"mix\")");
        StringAssert.Contains(mimeMessage.BodyStructure, "\"<pixel@example.net>\"");
        StringAssert.Contains(mimeMessage.BodyStructure, "\"dGVzdC1kaWdlc3Q=\"");
        StringAssert.Contains(
            mimeMessage.BodyStructure,
            "(\"INLINE\" (\"FILENAME\" \"pixel.png\"))");
        StringAssert.Contains(mimeMessage.BodyStructure, "(\"en\" \"fr\")");
        StringAssert.Contains(mimeMessage.BodyStructure, "\"images/pixel.png\"");

        StringAssert.Contains(mimeMessage.Body, "\"<pixel@example.net>\"");
        Assert.IsFalse(mimeMessage.Body.Contains("\"BOUNDARY\"", StringComparison.Ordinal));
        Assert.IsFalse(mimeMessage.Body.Contains("\"INLINE\"", StringComparison.Ordinal));
        Assert.IsFalse(mimeMessage.Body.Contains("dGVzdC1kaWdlc3Q=", StringComparison.Ordinal));
        Assert.IsFalse(mimeMessage.Body.Contains("images/pixel.png", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BinarySectionsDecodeLeafPartsAndNormalizeTextLineEndings()
    {
        const string message =
            "From: sender@example.net\r\n" +
            "To: recipient@example.net\r\n" +
            "Subject: decoded sections\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=outer\r\n\r\n" +
            "--outer\r\n" +
            "Content-Type: multipart/alternative; boundary=inner\r\n\r\n" +
            "--inner\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "one=0Atwo=0Dthree\r\n" +
            "--inner--\r\n" +
            "--outer\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "QQD/Cg==\r\n" +
            "--outer\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Transfer-Encoding: x-rot13\r\n\r\n" +
            "uryyb\r\n" +
            "--outer--\r\n";

        using var mimeMessage = ImapMimeMessage.TryParse(message);

        Assert.IsNotNull(mimeMessage);
        Assert.AreEqual(
            ImapBinarySectionStatus.Success,
            mimeMessage.GetBinarySection("1.1", out var text));
        CollectionAssert.AreEqual(
            MailWireEncoding.Instance.GetBytes("one\r\ntwo\r\nthree"),
            text.Content);
        Assert.IsFalse(text.RequiresLiteral8);

        Assert.AreEqual(
            ImapBinarySectionStatus.Success,
            mimeMessage.GetBinarySection("2", out var binary));
        CollectionAssert.AreEqual(new byte[] { 0x41, 0x00, 0xff, 0x0a }, binary.Content);
        Assert.IsTrue(binary.RequiresLiteral8);

        Assert.AreEqual(
            ImapBinarySectionStatus.NotLeaf,
            mimeMessage.GetBinarySection(string.Empty, out _));
        Assert.AreEqual(
            ImapBinarySectionStatus.NotLeaf,
            mimeMessage.GetBinarySection("1", out _));
        Assert.AreEqual(
            ImapBinarySectionStatus.UnknownTransferEncoding,
            mimeMessage.GetBinarySection("3", out _));
        Assert.AreEqual(
            ImapBinarySectionStatus.NotFound,
            mimeMessage.GetBinarySection("4", out _));
    }

    [TestMethod]
    public void BinaryEmptySectionSelectsSinglePartMessageBody()
    {
        const string message =
            "From: sender@example.net\r\n" +
            "To: recipient@example.net\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "single=20part\r\n";

        using var mimeMessage = ImapMimeMessage.TryParse(message);

        Assert.IsNotNull(mimeMessage);
        Assert.AreEqual(
            ImapBinarySectionStatus.Success,
            mimeMessage.GetBinarySection(string.Empty, out var root));
        Assert.AreEqual("single part\r\n", MailWireEncoding.Instance.GetString(root.Content));
        Assert.AreEqual(
            ImapBinarySectionStatus.Success,
            mimeMessage.GetBinarySection("1", out var numbered));
        CollectionAssert.AreEqual(root.Content, numbered.Content);
    }
}
