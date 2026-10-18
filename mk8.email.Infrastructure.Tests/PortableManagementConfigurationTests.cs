using System.Security.Cryptography;
using mk8.email.Configuration;
using Supprocom.Secrets;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest discovers this public configuration fixture by reflection.")]
public sealed class PortableManagementConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ProtectedManagementValuesRemainOneSnapshotWithOwnedPublicVerifier()
    {
        using var fixture = new Fixture();
        using var signer = RSA.Create(3072);
        fixture.Values["Management:ArchiveVerificationKeyPem"] = signer.ExportSubjectPublicKeyInfoPem();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var loaded = await PortableEnvironmentLoader.LoadManagementFromDirectoryAsync(fixture.Root.FullName,
            EnvironmentValidationRole.ApplicationWorker, TestContext.CancellationToken).ConfigureAwait(false);
        fixture.Values["Management:AccountPassword"] = "later-owned-password";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("synthetic-management-password", loaded.Management.RequireAccountPassword(), StringComparer.Ordinal);
        Assert.AreEqual("123456", loaded.Management.RequireTotpCode(), StringComparer.Ordinal);
        using var verifier = loaded.Management.CreateArchiveVerifier();
        Assert.AreSequenceEqual(signer.ExportSubjectPublicKeyInfo(), verifier.ExportSubjectPublicKeyInfo());
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root.FullName, ".installation.key")));
    }

    [TestMethod]
    public async Task ArchiveOnlyRoleLoadsWithoutDatabaseCredentialsAndRefusesCredentialMixing()
    {
        using var fixture = new Fixture();
        fixture.Values.Clear();
        fixture.Values["ArchiveConnectionString"] = "synthetic-archive-connection-value";
        fixture.Values["ArchiveContainerName"] = "owned-archives";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var loaded = await PortableEnvironmentLoader.LoadArchiveManagementFromDirectoryAsync(
            fixture.Root.FullName, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("synthetic-archive-connection-value", loaded.ArchiveConnectionString, StringComparer.Ordinal);
        Assert.IsNull(loaded.AccountPassword);
        fixture.Values["AccountPassword"] = "unrelated-owned-account-password";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PortableEnvironmentLoader.LoadArchiveManagementFromDirectoryAsync(fixture.Root.FullName, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("An archive-only collection must not contain unrelated management credentials or service settings.", failure.Message, StringComparer.Ordinal);
        Assert.DoesNotContain("unrelated-owned-account-password", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(EnvironmentValidationRole.Gateway)]
    [DataRow(EnvironmentValidationRole.ApplicationWorker)]
    public async Task ServiceRoleCannotAdmitManagementOnlyCredentials(EnvironmentValidationRole role)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PortableEnvironmentLoader.LoadFromDirectoryAsync(fixture.Root.FullName, role, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("Service roles must not contain management-only configuration values.", failure.Message, StringComparer.Ordinal);
        Assert.DoesNotContain("synthetic-management-password", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("Management:PasswordFile")]
    [DataRow("Management:ArchiveConnectionStringFile")]
    [DataRow("Management:ArchiveVerificationKeyPath")]
    public async Task LegacyManagementFileSettingsRefuseWithoutOpeningTheirTargets(string setting)
    {
        using var fixture = new Fixture();
        var absent = Path.Combine(fixture.Root.FullName, "never-created");
        fixture.Values[setting] = absent;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PortableEnvironmentLoader.LoadManagementFromDirectoryAsync(fixture.Root.FullName,
                EnvironmentValidationRole.ApplicationWorker, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("The portable configuration contains unknown or invalid settings.", failure.Message, StringComparer.Ordinal);
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(File.Exists(absent));
    }

    [TestMethod]
    public void PublicVerifierRejectsPrivateOrWeakMaterialWithoutEchoingIt()
    {
        using var weak = RSA.Create(2048);
        using var strong = RSA.Create(3072);
        foreach (var value in new[] { weak.ExportSubjectPublicKeyInfoPem(), strong.ExportPkcs8PrivateKeyPem(), "not-an-owned-public-key" })
        {
            var failure = Assert.ThrowsExactly<CryptographicException>(() =>
            {
                using var unexpected = new ManagementConfig { ArchiveVerificationKeyPem = value }.CreateArchiveVerifier();
            });
            Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
            Assert.IsNull(failure.InnerException);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("mk8-management-snapshot-");
        // Finite reserved names and synthetic values: no service or network is started.
        internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
        {
            ["Database:Host"] = "database.example.test",
            ["Database:Name"] = "owned_fixture",
            ["Database:Username"] = "owned_fixture",
            ["Database:Password"] = "synthetic-database-password",
            ["Smtp:Hostname"] = "mail.example.test",
            ["Smtp:RequireAuth"] = "false",
            ["Imap:EnableImap"] = "false",
            ["Dav:EnableDav"] = "false",
            ["Filtering:RspamdEndpoint"] = "http://127.0.0.1:11333/checkv2",
            ["Management:AccountPassword"] = "synthetic-management-password",
            ["Management:TotpCode"] = "123456",
        };

        internal Task WriteAsync(CancellationToken token) => File.WriteAllTextAsync(Path.Combine(Root.FullName, ".env"),
            SupprocomSecretDocument.Serialize(Values.Select(pair => new SupprocomSecretSetting(pair.Key, pair.Value))), token);

        public void Dispose()
        {
            Root.Delete(recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
