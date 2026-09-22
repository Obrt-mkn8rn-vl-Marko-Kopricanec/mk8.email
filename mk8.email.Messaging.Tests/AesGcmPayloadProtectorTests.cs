using System.Security.Cryptography;
using System.Text;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class AesGcmPayloadProtectorTests
{
    [TestMethod]
    public void PayloadRoundTripsAndTamperingFailsClosed()
    {
        using var protector = CreateProtector("current", "current-key");
        var payload = Encoding.UTF8.GetBytes("credential-bearing payload");
        var associatedData = Encoding.UTF8.GetBytes("bound metadata");
        var protectedPayload = protector.Protect(payload, associatedData);

        CollectionAssert.AreEqual(payload, protector.Unprotect(protectedPayload, associatedData));
        protectedPayload.Ciphertext[0] ^= 0x40;
        try
        {
            _ = protector.Unprotect(protectedPayload, associatedData);
            Assert.Fail("A tampered encrypted payload was accepted.");
        }
        catch (CryptographicException)
        {
            // AuthenticationTagMismatchException is the platform-specific fail-closed subtype.
        }
    }

    [TestMethod]
    public void RotatedKeyCanReadExistingTrafficWithoutBecomingActive()
    {
        using var oldProtector = CreateProtector("old", "old-key");
        var payload = Encoding.UTF8.GetBytes("preserved traffic");
        var associatedData = Encoding.UTF8.GetBytes("exchange");
        var protectedPayload = oldProtector.Protect(payload, associatedData);
        var active = Key("current", "current-key");
        var old = Key("old", "old-key");
        using var rotated = new AesGcmPayloadProtector(active, [old]);

        CollectionAssert.AreEqual(payload, rotated.Unprotect(protectedPayload, associatedData));
        Assert.AreEqual("current", rotated.Protect(payload, associatedData).KeyId);
    }

    internal static AesGcmPayloadProtector CreateProtector(string id, string material) =>
        new(Key(id, material));

    internal static MessagingEncryptionKey Key(string id, string material) =>
        new(id, SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}
