using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers this internal dependency control by reflection.")]
internal sealed class PkcsDependencyTests
{
    [TestMethod]
    public void PatchedDependencyVerifiesOwnedDetachedContentAndRejectsTampering()
    {
        var version = typeof(SignedCms).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.IsNotNull(version);
        Assert.StartsWith("10.0.13", version, StringComparison.Ordinal);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=cms.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var content = "finite owned CMS fixture"u8.ToArray();
        var signed = new SignedCms(new ContentInfo(content), detached: true);
        signed.ComputeSignature(new CmsSigner(certificate));
        var encoded = signed.Encode();
        var verified = new SignedCms(new ContentInfo(content), detached: true);
        verified.Decode(encoded);
        verified.CheckSignature(verifySignatureOnly: true);
        Assert.HasCount(1, verified.SignerInfos);
        var changed = new SignedCms(new ContentInfo("changed owned CMS fixture"u8.ToArray()), detached: true);
        changed.Decode(encoded);
        Assert.ThrowsExactly<CryptographicException>(() => changed.CheckSignature(verifySignatureOnly: true));
    }
}
