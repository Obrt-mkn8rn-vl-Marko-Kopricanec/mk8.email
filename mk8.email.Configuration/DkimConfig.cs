using System.Security.Cryptography;

namespace mk8.email.Configuration;

public sealed class DkimConfig
{
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPem { get; init; }
    public string Selector { get; init; } = "default";
    public bool EnableSigning { get; init; }

    /// <summary>Imports the protected startup value; the caller owns the returned key.</summary>
    public RSA CreateSigningKey()
    {
        if (PrivateKeyPath is not null)
            throw new InvalidOperationException("DKIM key-file settings are retired; use protected Dkim.PrivateKeyPem.");
        var pem = PrivateKeyPem;
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 32_768)
            throw new InvalidOperationException("Dkim.PrivateKeyPem must contain a bounded RSA private key.");
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize < 2048)
                throw new CryptographicException("The RSA key is too small.");
            var privateBytes = key.ExportPkcs8PrivateKey();
            CryptographicOperations.ZeroMemory(privateBytes);
            return key;
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new InvalidOperationException("Dkim.PrivateKeyPem must contain an RSA private key of at least 2048 bits.");
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal string? Validate(bool required)
    {
        if (PrivateKeyPath is not null)
            return "DKIM key-file settings are retired; use protected Dkim.PrivateKeyPem.";
        if (!required && string.IsNullOrEmpty(PrivateKeyPem)) return null;
        try
        {
            using var key = CreateSigningKey();
            return null;
        }
        catch (InvalidOperationException error) { return error.Message; }
    }
}
