using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.Repositories;
using mk8.email.Configuration;

namespace mk8.email.Gateway.Security;

// This is an in-memory projection of the already loaded role snapshot, not a
// second configuration provider or an OS/file-backed key store.
internal sealed class SnapshotKeyRepository : IXmlRepository
{
    private readonly XElement[] _elements;

    internal SnapshotKeyRepository(IReadOnlyList<DataProtectionKeyConfig> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count is < 1 or > 64 || keys.Any(key => key is null)
            || keys.Select(key => key.Id).ToHashSet().Count != keys.Count)
        {
            throw new InvalidOperationException("The admin-cookie snapshot requires from one through 64 distinct keys.");
        }
        var now = DateTimeOffset.UtcNow;
        if (!keys.Any(key => key.RevokedAt is null && key.ActivatesAt <= now && now < key.ExpiresAt))
            throw new InvalidOperationException("The admin-cookie snapshot has no currently active non-revoked key.");
        var elements = new List<XElement>(keys.Count * 2);
        foreach (var key in keys)
        {
            elements.Add(CreateElement(key));
            if (key.RevokedAt is { } revoked)
            {
                elements.Add(new XElement("revocation", new XAttribute("version", 1),
                    new XElement("revocationDate", revoked), new XElement("key", new XAttribute("id", key.Id)),
                    new XElement("reason", "Revoked in the protected role snapshot.")));
            }
        }
        _elements = [.. elements];
    }

    // IXmlRepository fixes this return type; a different interface would not
    // implement the framework contract. The copied result is already read-only.
#pragma warning disable HLQ003
    public IReadOnlyCollection<XElement> GetAllElements() =>
        Array.AsReadOnly(_elements.Select(element => new XElement(element)).ToArray());
#pragma warning restore HLQ003

    public void StoreElement(XElement element, string friendlyName) =>
        throw new InvalidOperationException("Admin-cookie keys are read-only startup values; update the protected role collection explicitly.");

    private static XElement CreateElement(DataProtectionKeyConfig key)
    {
        var bytes = key.DecodeMasterKey();
        try
        {
            using var secret = new Secret(bytes);
            var descriptor = new AuthenticatedEncryptorDescriptor(new AuthenticatedEncryptorConfiguration
            {
                EncryptionAlgorithm = EncryptionAlgorithm.AES_256_CBC,
                ValidationAlgorithm = ValidationAlgorithm.HMACSHA256,
            }, secret).ExportToXml();
            return new XElement("key", new XAttribute("id", key.Id), new XAttribute("version", 1),
                new XElement("creationDate", key.CreatedAt), new XElement("activationDate", key.ActivatesAt),
                new XElement("expirationDate", key.ExpiresAt),
                new XElement("descriptor",
                    new XAttribute("deserializerType", descriptor.DeserializerType.AssemblyQualifiedName
                        ?? throw new InvalidOperationException("The built-in cookie-key descriptor is unavailable.")),
                    descriptor.SerializedDescriptorElement));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
