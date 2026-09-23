using System.Text;
using mk8.email.MailWire;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class Pop3WireCodecTests
{
    [TestMethod]
    public void NormalizedSizeMatchesWireBytesForMixedLineEndings()
    {
        foreach (var source in new[]
                 {
                     Array.Empty<byte>(),
                     "line"u8.ToArray(),
                     "line\n"u8.ToArray(),
                     "line\r"u8.ToArray(),
                     "line\r\n"u8.ToArray(),
                     "a\n.b\r\nc\rd"u8.ToArray(),
                 })
        {
            var normalized = Pop3WireCodec.NormalizeCrlf(source);
            Assert.AreEqual(normalized.Length, Pop3WireCodec.GetNormalizedCrlfLength(source));
            Assert.IsTrue(normalized.AsSpan().EndsWith("\r\n"u8));
        }

        CollectionAssert.AreEqual(
            "a\r\n.b\r\nc\r\nd\r\n"u8.ToArray(),
            Pop3WireCodec.NormalizeCrlf("a\n.b\r\nc\rd"u8));
    }

    [TestMethod]
    public void TopReturnsHeadersAndOnlyTheRequestedBodyLines()
    {
        var message = "Subject: example\r\n\r\nfirst\r\nsecond\r\n"u8.ToArray();
        CollectionAssert.AreEqual(
            "Subject: example\r\n\r\n"u8.ToArray(),
            Pop3WireCodec.TakeTop(message, 0));
        CollectionAssert.AreEqual(
            "Subject: example\r\n\r\nfirst\r\n"u8.ToArray(),
            Pop3WireCodec.TakeTop(message, 1));
        CollectionAssert.AreEqual(message, Pop3WireCodec.TakeTop(message, 9));
        CollectionAssert.AreEqual("no headers"u8.ToArray(),
            Pop3WireCodec.TakeTop("no headers"u8.ToArray(), 1));
    }

    [TestMethod]
    public async Task DotStuffingPreservesOctetsAndEscapesLineInitialDots()
    {
        await using var destination = new MemoryStream();
        await Pop3WireCodec.WriteDotStuffedAsync(
            destination,
            ".start\r\n..second\r\nlast\r\n"u8.ToArray(),
            CancellationToken.None);
        Assert.AreEqual(
            "..start\r\n...second\r\nlast\r\n.\r\n",
            Encoding.ASCII.GetString(destination.ToArray()));
    }
}
