using mk8.email.MailWire;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapUidSetParserTests
{
    [TestMethod]
    public void UidSetParserPreservesStarAndReversedRangesForWorkerResolution()
    {
        Assert.IsTrue(ImapUidSetParser.TryParse("*,7:4,2:*,2147483647", out var ranges));
        CollectionAssert.AreEqual(new[]
        {
            new ImapUidSetRange(null, null),
            new ImapUidSetRange(7, 4),
            new ImapUidSetRange(2, null),
            new ImapUidSetRange(int.MaxValue, int.MaxValue),
        }, ranges);
    }

    [TestMethod]
    public void UidSetParserRejectsMalformedOrNonAsciiValues()
    {
        foreach (var value in new[]
                 {
                     "", "0", "1:", ":1", "1::2", "1,,2", "+1", "-1", "1 ",
                     "*x", "١", "2147483648", "$",
                 })
        {
            Assert.IsFalse(ImapUidSetParser.TryParse(value, out _), value);
        }
    }
}
