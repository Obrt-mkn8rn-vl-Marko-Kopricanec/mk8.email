using mk8.email.MailWire;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MailWireBoundaryTests
{
    [TestMethod]
    public void PublicParsersRejectNullInputsWithoutDereferencingThem()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ImapFlagSyntax.TryValidate(null!, out _));
        Assert.IsFalse(OAuthSasl.TryParseXOAuth2(null!, out _, out _));
    }

    [TestMethod]
    public void ImapUidSetParserPreservesItsPublishedListResult()
    {
        Assert.IsTrue(ImapUidSetParser.TryParse("1:3,*,7", out var ranges));
        CollectionAssert.AreEqual(
            new[]
            {
                new ImapUidSetRange(1, 3),
                new ImapUidSetRange(null, null),
                new ImapUidSetRange(7, 7),
            },
            ranges);
    }
}
