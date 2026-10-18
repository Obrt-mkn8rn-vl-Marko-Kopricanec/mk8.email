using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest discovers this public fixture by reflection.")]
public sealed class TlsSnapshotTests
{
    [TestMethod]
    public void DisabledTlsAcceptsBlankOutlineButNotAnOrphanPassword()
    {
        var blank = new TlsConfig { CertificatePkcs12 = string.Empty, CertificatePassword = string.Empty };
        Assert.IsNull(blank.Validate(required: false));
        Assert.IsNotNull(blank.Validate(required: true));
        var orphan = new TlsConfig { CertificatePassword = "synthetic-orphan-password" };
        var error = orphan.Validate(required: false);
        Assert.IsNotNull(error);
        Assert.IsFalse(error.Contains("synthetic-orphan-password", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("synthetic-fixture-password")]
    public void SnapshotCreatesIndependentPrivateKeyCertificates(string? password)
    {
        using var original = CreateCertificate();
        var config = new TlsConfig
        {
            CertificatePkcs12 = Convert.ToBase64String(original.Export(X509ContentType.Pkcs12, password)),
            CertificatePassword = password,
        };
        using var first = config.CreateCertificate();
        using var second = config.CreateCertificate();
        Assert.AreNotSame(first, second);
        Assert.IsTrue(first.HasPrivateKey);
        Assert.IsTrue(second.HasPrivateKey);
        Assert.AreEqual(original.Thumbprint, first.Thumbprint, StringComparer.Ordinal);
        Assert.AreEqual(original.Thumbprint, second.Thumbprint, StringComparer.Ordinal);
#pragma warning disable MA0150 // Explicitly verify that ordinary object formatting cannot reveal the private PKCS#12/password snapshot.
        Assert.AreEqual(typeof(TlsConfig).FullName, config.ToString(), StringComparer.Ordinal);
#pragma warning restore MA0150
    }

    [TestMethod]
    [DataRow("invalid-base64", null, null)]
    [DataRow("", null, null)]
    [DataRow("c3ludGhldGlj\n", null, null)]
    [DataRow(null, "private-fixture-cert.pfx", null)]
    [DataRow(null, null, "private-fixture-key.pem")]
    public void InvalidValuesAndRetiredLocatorsRefuseWithoutDisclosure(string? value, string? certificate, string? key)
    {
        var config = new TlsConfig { CertificatePkcs12 = value, CertificatePath = certificate, CertificateKeyPath = key };
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => { using var unexpected = config.CreateCertificate(); });
        Assert.IsNull(failure.InnerException);
        foreach (var secret in new[] { value, certificate, key })
        {
            if (!string.IsNullOrEmpty(secret)) Assert.IsFalse(failure.ToString().Contains(secret, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void WrongPasswordAndCertificateWithoutPrivateKeyAreRefused()
    {
        using var original = CreateCertificate();
        var protectedValue = new TlsConfig
        {
            CertificatePkcs12 = Convert.ToBase64String(original.Export(X509ContentType.Pkcs12, "synthetic-correct-password")),
            CertificatePassword = "synthetic-wrong-password",
        };
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => { using var unexpected = protectedValue.CreateCertificate(); });
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.Message.Contains("synthetic-wrong-password", StringComparison.Ordinal));
        using var publicOnly = X509CertificateLoader.LoadCertificate(original.RawData);
        var withoutKey = new TlsConfig { CertificatePkcs12 = Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12)) };
        Assert.ThrowsExactly<InvalidOperationException>(() => { using var unexpected = withoutKey.CreateCertificate(); });
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
}
