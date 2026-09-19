using mk8.email.Application.Protocol;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapMailboxEncodingTests
{
    [TestMethod]
    public void EncodeUsesCanonicalModifiedUtf7()
    {
        Assert.AreEqual(
            "~peter/mail/&U,BTFw-/&ZeVnLIqe-/A&-B",
            ImapMailboxEncoding.Encode("~peter/mail/台北/日本語/A&B"));
    }

    [TestMethod]
    public void DecodeAcceptsCanonicalModifiedUtf7()
    {
        Assert.IsTrue(ImapMailboxEncoding.TryDecode(
            "~peter/mail/&U,BTFw-/&ZeVnLIqe-/A&-B",
            out var decoded));
        Assert.AreEqual("~peter/mail/台北/日本語/A&B", decoded);
    }

    [TestMethod]
    [DataRow("&")]
    [DataRow("&A-")]
    [DataRow("&AEE-")]
    [DataRow("&Jjo!-")]
    [DataRow("é")]
    public void DecodeRejectsInvalidOrNonCanonicalWireNames(string value)
    {
        Assert.IsFalse(ImapMailboxEncoding.TryDecode(value, out _));
    }
}
