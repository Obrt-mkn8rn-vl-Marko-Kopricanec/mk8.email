using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using Org.BouncyCastle.Crypto;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DkimSigningTests
{
    private const string RawMessage =
        "From: Sender <sender@tenant.example.test>\r\n" +
        "To: Recipient <recipient@example.com>\r\n" +
        "Subject: DKIM test\r\n" +
        "Date: Thu, 03 Sep 2026 12:00:00 +0200\r\n" +
        "Message-ID: <dkim-test@tenant.example.test>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "Hello DKIM.\r\n";

    private string _testDirectory = null!;
    private TestDkimKey _key = null!;

    [TestMethod]
    public void SigningExceptionProvidesStandardConstructors()
    {
        Assert.IsNotNull(new DkimSigningException().Message);
        Assert.AreEqual("missing key", new DkimSigningException("missing key").Message, StringComparer.Ordinal);
        var cause = new InvalidOperationException("invalid key");
        Assert.AreSame(cause, new DkimSigningException("signing failed", cause).InnerException);
    }

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-dkim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _key = TestCertificateFactory.CreateDkimKey(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    public void SignatureVerifiesWithPublishedPublicKey()
    {
        var signer = new MimeKitDkimSigningService();

        var signed = signer.Sign(RawMessage, "tenant.example.test", "default", _key.PrivateKeyPath);
        using var message = LoadMessage(signed);
        var signatureIndex = message.Headers.IndexOf(HeaderId.DkimSignature);

        Assert.IsGreaterThanOrEqualTo(0, signatureIndex);
        var signature = message.Headers[signatureIndex];
        StringAssert.Contains(signature.Value, "a=rsa-sha256", StringComparison.Ordinal);
        StringAssert.Contains(signature.Value, "c=relaxed/relaxed", StringComparison.Ordinal);
        StringAssert.Contains(signature.Value, "d=tenant.example.test", StringComparison.Ordinal);
        StringAssert.Contains(signature.Value, "s=default", StringComparison.Ordinal);

        var verifier = new DkimVerifier(new TestPublicKeyLocator(_key.PublicDnsRecord));
        Assert.IsTrue(verifier.Verify(message, signature));
    }

    [TestMethod]
    public void BodyChangeInvalidatesSignature()
    {
        var signer = new MimeKitDkimSigningService();
        var signed = signer.Sign(RawMessage, "tenant.example.test", "default", _key.PrivateKeyPath);
        var changed = signed.Replace("Hello DKIM.", "Changed body.", StringComparison.Ordinal);
        using var message = LoadMessage(changed);
        var signature = message.Headers[message.Headers.IndexOf(HeaderId.DkimSignature)];
        var verifier = new DkimVerifier(new TestPublicKeyLocator(_key.PublicDnsRecord));

        Assert.IsFalse(verifier.Verify(message, signature));
    }

    [TestMethod]
    public void InvalidPrivateKeyStopsSigning()
    {
        var invalidKeyPath = Path.Combine(_testDirectory, "invalid.pem");
        File.WriteAllText(invalidKeyPath, "not a private key");
        var signer = new MimeKitDkimSigningService();

        var exception = Assert.ThrowsExactly<DkimSigningException>(
            () => signer.Sign(RawMessage, "tenant.example.test", "default", invalidKeyPath));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void MissingFromHeaderStopsSigning()
    {
        var signer = new MimeKitDkimSigningService();

        Assert.ThrowsExactly<DkimSigningException>(
            () => signer.Sign("Subject: Missing From\r\n\r\nbody", "tenant.example.test", "default", _key.PrivateKeyPath));
    }

    [TestMethod]
    public void SignPreservesEightBitMessageOctets()
    {
        var signer = new MimeKitDkimSigningService();
        var utf8Text = Encoding.UTF8.GetBytes("café");
        var wireText = Encoding.Latin1.GetString(utf8Text);
        var rawMessage =
            "From: admin@tenant.example.test\r\n" +
            "To: recipient@example.net\r\n" +
            "Subject: UTF-8 body\r\n" +
            "Date: Thu, 04 Sep 2026 12:00:00 +0000\r\n" +
            "Message-ID: <utf8-test@tenant.example.test>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n" +
            wireText + "\r\n";

        var signed = signer.Sign(rawMessage, "tenant.example.test", "default", _key.PrivateKeyPath);

        Assert.IsTrue(Encoding.Latin1.GetBytes(signed).AsSpan().IndexOf(utf8Text) >= 0);
        using var message = LoadMessage(signed);
        var signature = message.Headers[message.Headers.IndexOf(HeaderId.DkimSignature)];
        var verifier = new DkimVerifier(new TestPublicKeyLocator(_key.PublicDnsRecord));
        Assert.IsTrue(verifier.Verify(message, signature));
    }

    private static MimeMessage LoadMessage(string rawMessage)
    {
        var stream = new MemoryStream(Encoding.Latin1.GetBytes(rawMessage), writable: false);
        try
        {
            return MimeMessage.Load(stream);
        }
        finally
        {
            stream.Dispose();
        }
    }

    private sealed class TestPublicKeyLocator(string publicDnsRecord) : DkimPublicKeyLocatorBase
    {
        public override AsymmetricKeyParameter LocatePublicKey(
            string methods,
            string domain,
            string selector,
            CancellationToken cancellationToken = default) => GetPublicKey(publicDnsRecord);

        public override Task<AsymmetricKeyParameter> LocatePublicKeyAsync(
            string methods,
            string domain,
            string selector,
            CancellationToken cancellationToken = default) => Task.FromResult(GetPublicKey(publicDnsRecord));
    }
}
