using mk8.email.MailWire;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapMailboxEncodingTests
{
    [TestMethod]
    public void EncodeUsesCanonicalModifiedUtf7()
    {
        Assert.AreEqual(
            "~peter/mail/&U,BTFw-/&ZeVnLIqe-/A&-B",
            ImapMailboxEncoding.Encode("~peter/mail/台北/日本語/A&B"), StringComparer.Ordinal);
    }

    [TestMethod]
    public void DecodeAcceptsCanonicalModifiedUtf7()
    {
        Assert.IsTrue(ImapMailboxEncoding.TryDecode(
            "~peter/mail/&U,BTFw-/&ZeVnLIqe-/A&-B",
            out var decoded));
        Assert.AreEqual("~peter/mail/台北/日本語/A&B", decoded, StringComparer.Ordinal);
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
