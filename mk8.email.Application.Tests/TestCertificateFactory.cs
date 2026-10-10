using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace mk8.email.Application.Tests;

internal static class TestCertificateFactory
{
    private static readonly ConcurrentDictionary<string, byte> Certificates = new(StringComparer.Ordinal);

    public static bool IsTrusted(X509Certificate? certificate, SslPolicyErrors errors) =>
        certificate is not null
        && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
        && Certificates.ContainsKey(certificate.GetCertHashString(HashAlgorithmName.SHA256));

    public static string Create(string directory, string hostName = "email.tenant.example.test")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(directory, $"{hostName}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        Certificates.TryAdd(certificate.GetCertHashString(HashAlgorithmName.SHA256), 0);
        return path;
    }

    public static TestDkimKey CreateDkimKey(string directory)
    {
        using var key = RSA.Create(2048);
        var privateKeyPath = Path.Combine(directory, "dkim-private.pem");
        File.WriteAllText(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return new TestDkimKey(privateKeyPath, $"v=DKIM1; k=rsa; p={publicKey}");
    }
}
