using System.Security.Cryptography;
using System.Text;
using mk8.email.Configuration;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class AesGcmPayloadProtectorTests
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
        Assert.AreEqual("current", rotated.Protect(payload, associatedData).KeyId, StringComparer.Ordinal);
    }

    internal static AesGcmPayloadProtector CreateProtector(string id, string material) =>
        new(Key(id, material));

    internal static MessagingEncryptionKey Key(string id, string material) =>
        new(id, SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    [TestMethod]
    [DataRow("primary:2026", false)]
    [DataRow("primary@2026", false)]
    [DataRow("primary/2026", false)]
    [DataRow("primary\n", false)]
    [DataRow(" primary", false)]
    [DataRow("-primary", false)]
    [DataRow("", false)]
    [DataRow("primary.2026_01-old", true)]
    [DataRow("0", true)]
    public void SnapshotAndProtectorAdmitTheSameEncryptionIdentifiers(string id, bool accepted)
    {
        AssertKeyAdmission(id, accepted);
    }

    [TestMethod]
    public void EncryptionIdentifierLengthBoundaryDoesNotChangeWorkerIdentityGrammar()
    {
        AssertKeyAdmission(new string('a', 64), accepted: true);
        AssertKeyAdmission(new string('a', 65), accepted: false);
    }

    private static void AssertKeyAdmission(string id, bool accepted)
    {
        var material = Convert.ToBase64String(new byte[32]);
        var configuration = new EnvironmentConfig
        {
            Messaging = new MessagingConfig
            {
                Enabled = true,
                EncryptionKeyId = id,
                EncryptionKey = material,
                DecryptionKeys = [new MessagingDecryptionKeyConfig { Id = id, Key = material }],
                WorkerId = "worker:2026@fixture/role",
            },
        };
        var errors = configuration.Validate(isDevelopment: true);
        Assert.AreEqual(!accepted, errors.Contains("Messaging.EncryptionKeyId is invalid.", StringComparer.Ordinal));
        Assert.AreEqual(!accepted, errors.Contains("A Messaging.DecryptionKeys identifier is invalid.", StringComparer.Ordinal));
        Assert.DoesNotContain("Messaging.WorkerId is invalid.", errors, StringComparer.Ordinal);
        if (accepted)
        {
            using var primary = new AesGcmPayloadProtector(Key(id, "fixture-key"));
            using var historical = new AesGcmPayloadProtector(Key("active", "fixture-key"), [Key(id, "fixture-key")]);
            Assert.AreEqual(id, primary.Protect([], []).KeyId, StringComparer.Ordinal);
        }
        else
        {
            Assert.ThrowsExactly<ArgumentException>(() => new AesGcmPayloadProtector(Key(id, "fixture-key")));
            Assert.ThrowsExactly<ArgumentException>(() => new AesGcmPayloadProtector(Key("active", "fixture-key"), [Key(id, "fixture-key")]));
        }
    }
}
