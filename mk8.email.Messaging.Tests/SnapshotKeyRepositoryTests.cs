using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using mk8.email.Configuration;
using mk8.email.Gateway.Security;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers these internal finite keyring controls by reflection.")]
internal sealed class SnapshotKeyRepositoryTests
{
    [TestMethod]
    public void IndependentProviderRestartsDecryptWithOnlyTheRetainedSnapshot()
    {
        var key = PortableCookieKeyFixture.Create();
        var configuration = new AdminConfig { DataProtectionKeys = [key] };
        string ciphertext;
        using (var first = CreateProvider(configuration))
        {
            GatewayDataProtection.RequireAvailable(first);
            ciphertext = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("owned-cookie").Protect("owned-fixture-payload");
            Assert.DoesNotContain("owned-fixture-payload", ciphertext, StringComparison.Ordinal);
            Assert.AreEqual(key.Id, first.GetRequiredService<IKeyManager>().GetAllKeys().Single().KeyId);
        }
        using var second = CreateProvider(configuration);
        GatewayDataProtection.RequireAvailable(second);
        var provider = second.GetRequiredService<IDataProtectionProvider>();
        Assert.AreEqual("owned-fixture-payload", provider.CreateProtector("owned-cookie").Unprotect(ciphertext), StringComparer.Ordinal);
        Assert.ThrowsExactly<CryptographicException>(() => provider.CreateProtector("other-purpose").Unprotect(ciphertext));
        var options = second.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        Assert.IsFalse(options.AutoGenerateKeys);
        Assert.IsInstanceOfType<SnapshotKeyRepository>(options.XmlRepository);
        Assert.IsNull(options.XmlEncryptor);
        Assert.ThrowsExactly<InvalidOperationException>(() => second.GetRequiredService<IKeyManager>()
            .CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));
        Assert.HasCount(1, second.GetRequiredService<IKeyManager>().GetAllKeys());
    }

    [TestMethod]
    public void RotationRetainsOldDecryptButExplicitRevocationRefusesIt()
    {
        var old = PortableCookieKeyFixture.Create();
        string ciphertext;
        using (var initial = CreateProvider(new AdminConfig { DataProtectionKeys = [old] }))
            ciphertext = initial.GetRequiredService<IDataProtectionProvider>().CreateProtector("owned-cookie").Protect("before-rotation");
        var current = PortableCookieKeyFixture.Create();
        using (var rotated = CreateProvider(new AdminConfig { DataProtectionKeys = [old, current] }))
        {
            Assert.AreEqual("before-rotation", rotated.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("owned-cookie").Unprotect(ciphertext), StringComparer.Ordinal);
        }
        var revoked = new DataProtectionKeyConfig
        {
            Id = old.Id,
            CreatedAt = old.CreatedAt,
            ActivatesAt = old.ActivatesAt,
            ExpiresAt = old.ExpiresAt,
            MasterKey = old.MasterKey,
            RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        using var replacement = CreateProvider(new AdminConfig { DataProtectionKeys = [revoked, current] });
        var protector = replacement.GetRequiredService<IDataProtectionProvider>().CreateProtector("owned-cookie");
        Assert.ThrowsExactly<CryptographicException>(() => protector.Unprotect(ciphertext));
        Assert.AreEqual("after-revocation", protector.Unprotect(protector.Protect("after-revocation")), StringComparer.Ordinal);
    }

    [TestMethod]
    public void RepositoryOwnsItsProjectionAndRefusesEveryWrite()
    {
        var key = PortableCookieKeyFixture.Create();
        DataProtectionKeyConfig[] inputs = [key];
        var repository = new SnapshotKeyRepository(inputs);
        inputs[0] = PortableCookieKeyFixture.Create();
        var original = repository.GetAllElements().Single();
        Assert.AreEqual(key.Id.ToString("D"), original.Attribute("id")?.Value, StringComparer.Ordinal);
        original.RemoveNodes();
        var retained = repository.GetAllElements().Single();
        Assert.IsNotNull(retained.Element("descriptor"));
        Assert.ThrowsExactly<InvalidOperationException>(() => repository.StoreElement(new XElement("key"), "ignored"));
        Assert.AreEqual(retained.ToString(SaveOptions.DisableFormatting),
            repository.GetAllElements().Single().ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal);
    }

    [TestMethod]
    public void MissingExpiredRevokedAndRetiredDirectoryInputsCannotStartAKeyring()
    {
        var key = PortableCookieKeyFixture.Create();
        Assert.ThrowsExactly<InvalidOperationException>(() => new SnapshotKeyRepository([]));
        Assert.ThrowsExactly<InvalidOperationException>(() => new SnapshotKeyRepository([key, key]));
        var expired = new DataProtectionKeyConfig
        {
            Id = key.Id,
            CreatedAt = key.CreatedAt,
            ActivatesAt = key.ActivatesAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            MasterKey = key.MasterKey,
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => new SnapshotKeyRepository([expired]));
        var revoked = new DataProtectionKeyConfig
        {
            Id = key.Id,
            CreatedAt = key.CreatedAt,
            ActivatesAt = key.ActivatesAt,
            ExpiresAt = key.ExpiresAt,
            MasterKey = key.MasterKey,
            RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => new SnapshotKeyRepository([revoked]));
        using var root = new OwnedDirectory();
        var retired = Path.Combine(root.Path, "never-created-keyring");
        var services = new ServiceCollection();
        Assert.ThrowsExactly<InvalidOperationException>(() => GatewayDataProtection.AddSnapshot(services,
            new AdminConfig { DataProtectionKeyPath = retired, DataProtectionKeys = [key] }));
        Assert.IsFalse(Directory.Exists(retired));
        Assert.IsEmpty(services);
    }

    private static ServiceProvider CreateProvider(AdminConfig configuration)
    {
        var services = new ServiceCollection().AddLogging();
        GatewayDataProtection.AddSnapshot(services, configuration);
        return services.BuildServiceProvider();
    }

    private sealed class OwnedDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("mk8-keyring-refusal-");
        internal string Path => _directory.FullName;
        public void Dispose()
        {
            _directory.Delete(recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
