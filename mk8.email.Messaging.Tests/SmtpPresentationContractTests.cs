using System.Text.Json;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class SmtpPresentationContractTests
{
    [TestMethod]
    public void RelayRequestPreservesWireBytesAndDeliveryOptionsAcrossJson()
    {
        var original = new SmtpRelayPresentationRequest(
            "sender@example.test",
            "recipient@example.test",
            "Subject: Eight bit\r\n\r\n" + (char)0xff + "\r\n",
            new OutboundMailOptions(
                RequiresSmtpUtf8: true,
                new MailDsnEnvelope("HDRS", "job+2B42"),
                new MailDsnRecipient("FAILURE,DELAY", "rfc822;recipient@example.test")));

        var payload = JsonSerializer.SerializeToUtf8Bytes(original);
        var decoded = JsonSerializer.Deserialize<SmtpRelayPresentationRequest>(payload);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(original.Sender, decoded.Sender, StringComparer.Ordinal);
        Assert.AreEqual(original.Recipient, decoded.Recipient, StringComparer.Ordinal);
        Assert.AreEqual(original.RawMessage, decoded.RawMessage, StringComparer.Ordinal);
        Assert.AreEqual(original.Options, decoded.Options);
    }
}
