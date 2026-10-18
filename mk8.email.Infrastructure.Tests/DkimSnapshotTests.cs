using System.Security.Cryptography;
using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest discovers this public fixture by reflection.")]
public sealed class DkimSnapshotTests
{
    [TestMethod]
    [DataRow("synthetic-invalid-key", null)]
    [DataRow("", null)]
    [DataRow(null, "never-read-private-fixture.pem")]
    public void InvalidMaterialAndRetiredLocatorRefuseWithoutDisclosure(string? material, string? path)
    {
        var config = new DkimConfig { PrivateKeyPem = material, PrivateKeyPath = path };
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => { using var unexpected = config.CreateSigningKey(); });
        Assert.IsNull(failure.InnerException);
        foreach (var value in new[] { material, path })
        {
            if (!string.IsNullOrEmpty(value)) Assert.IsFalse(failure.ToString().Contains(value, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow(1024, true)]
    [DataRow(2048, false)]
    public void WeakPrivateKeysAndPublicOnlyKeysAreRefused(int bits, bool includePrivate)
    {
        using var key = RSA.Create(bits);
        var config = new DkimConfig { PrivateKeyPem = includePrivate ? key.ExportPkcs8PrivateKeyPem() : key.ExportSubjectPublicKeyInfoPem() };
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => { using var unexpected = config.CreateSigningKey(); });
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.Message.Contains("BEGIN", StringComparison.Ordinal));
    }
}
