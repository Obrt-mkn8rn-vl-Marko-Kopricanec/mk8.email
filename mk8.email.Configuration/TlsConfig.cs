using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace mk8.email.Configuration;

public sealed class TlsConfig
{
    // Retained for strict refusal of old profiles, never read as file locators.
    public string? CertificatePath { get; init; }
    public string? CertificateKeyPath { get; init; }
    public string? CertificatePkcs12 { get; init; }
    public string? CertificatePassword { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCertificate => !string.IsNullOrWhiteSpace(CertificatePkcs12);

    /// <summary>Creates an independently owned in-memory certificate from the startup snapshot.</summary>
    public X509Certificate2 CreateCertificate()
    {
        RefuseFileLocators();
        var encoded = CertificatePkcs12;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 174_764)
            throw new InvalidOperationException("Tls.CertificatePkcs12 must contain a base64 PKCS#12 value of at most 128 KiB.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            throw new InvalidOperationException("Tls.CertificatePkcs12 must contain canonical padded base64.");
        }
        try
        {
            if (bytes.Length is 0 or > 131_072
                || !string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal))
                throw new InvalidOperationException("Tls.CertificatePkcs12 must contain canonical padded base64 of at most 128 KiB.");
            return ImportCertificate(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal string? Validate(bool required)
    {
        try
        {
            RefuseFileLocators();
            if (!required && !HasCertificate && string.IsNullOrEmpty(CertificatePassword)) return null;
            using var certificate = CreateCertificate();
            return null;
        }
        catch (InvalidOperationException exception) { return exception.Message; }
    }

    private X509Certificate2 ImportCertificate(byte[] bytes)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(bytes, CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            // Provider diagnostics and password/PKCS#12 contents are not startup output.
            throw new InvalidOperationException("The configured TLS certificate or password is invalid.");
        }
        try
        {
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("The configured TLS certificate must include its private key.");
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private void RefuseFileLocators()
    {
        if (CertificatePath is not null || CertificateKeyPath is not null)
            throw new InvalidOperationException("TLS certificate file settings are retired; use protected Tls.CertificatePkcs12.");
    }
}
