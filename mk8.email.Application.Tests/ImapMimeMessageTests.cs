using mk8.email.Application.Protocol;

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
}
