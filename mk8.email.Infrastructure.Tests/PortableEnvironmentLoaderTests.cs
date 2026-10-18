using mk8.email.Configuration;
using Supprocom.Secrets;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1515", Justification = "MSTest requires a public fixture for discovery.")]
public sealed class PortableEnvironmentLoaderTests
{
    private static readonly string[] RemovedActiveFiles = [".env.template"];
    private static readonly string[] TruncatedActiveFiles = [".env", ".env.template"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task WorkerRefusesGatewayTlsMaterialBeforeCertificateImport()
    {
        using var fixture = new SettingsFixture(includeCookieKeys: false);
        fixture.Values["Tls:CertificatePkcs12"] = "synthetic-gateway-only-material";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LoadAsync(fixture, EnvironmentValidationRole.ApplicationWorker)).ConfigureAwait(false);
        Assert.AreEqual("Worker configuration must not contain Gateway TLS material.", failure.Message, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ProtectedTlsMaterialSurvivesDocumentReplacementAsOneStartupSnapshot()
    {
        using var fixture = new SettingsFixture();
        using var expected = TlsSnapshotTests.CreateCertificate();
        var encoded = Convert.ToBase64String(expected.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12));
        fixture.Values["Tls:CertificatePkcs12"] = encoded;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(fixture, EnvironmentValidationRole.Gateway).ConfigureAwait(false);
        var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Root.FullName, ".env"), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(protectedBytes).Contains(encoded, StringComparison.Ordinal));
        fixture.Values["Tls:CertificatePkcs12"] = "later-invalid-certificate";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var retained = loaded.Tls.CreateCertificate();
        Assert.AreEqual(expected.Thumbprint, retained.Thumbprint, StringComparer.Ordinal);
        Assert.IsTrue(retained.HasPrivateKey);
    }

    [TestMethod]
    public async Task ProtectedDkimMaterialRemainsAnIndependentWorkerStartupSnapshot()
    {
        using var fixture = new SettingsFixture(includeCookieKeys: false);
        using var expected = System.Security.Cryptography.RSA.Create(2048);
        fixture.Values["Dkim:EnableSigning"] = "true";
        fixture.Values["Dkim:PrivateKeyPem"] = expected.ExportPkcs8PrivateKeyPem();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(fixture, EnvironmentValidationRole.ApplicationWorker).ConfigureAwait(false);
        var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Root.FullName, ".env"), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(protectedBytes).Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal));
        fixture.Values["Dkim:PrivateKeyPem"] = "later-invalid-key";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var retained = loaded.Dkim.CreateSigningKey();
        Assert.AreSequenceEqual(expected.ExportSubjectPublicKeyInfo(), retained.ExportSubjectPublicKeyInfo());
        var challenge = "finite-synthetic-signature-control"u8.ToArray();
        var signature = retained.SignData(challenge, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        Assert.IsTrue(expected.VerifyData(challenge, signature, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
    }

    [TestMethod]
    [DataRow(EnvironmentValidationRole.Gateway)]
    [DataRow(EnvironmentValidationRole.ApplicationWorker)]
    public async Task PortableValuesAreValidatedAndRemainAStartupSnapshot(EnvironmentValidationRole role)
    {
        using var fixture = new SettingsFixture(includeCookieKeys: role is not EnvironmentValidationRole.ApplicationWorker);
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(fixture, role).ConfigureAwait(false);
        fixture.Values["Database:Password"] = "later-synthetic-secret";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual("synthetic-database-password", loaded.Database.Password, StringComparer.Ordinal);
        Assert.AreEqual("database.example.test", loaded.Database.Host, StringComparer.Ordinal);
        Assert.IsEmpty(loaded.Validate(role: role));
    }

    [TestMethod]
    public async Task ProcessEnvironmentCannotOverrideValuesOrSelectDevelopment()
    {
        using var fixture = new SettingsFixture();
        fixture.Values["Database:Password"] = "short";
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var names = new[] { "Database__Password", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" };
        var originals = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "synthetic-environment-secret");
            Environment.SetEnvironmentVariable(names[1], "Development");
            Environment.SetEnvironmentVariable(names[2], "Development");
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                LoadAsync(fixture)).ConfigureAwait(false);
            Assert.Contains("Database.Password", failure.Message, StringComparison.Ordinal);
            Assert.IsFalse(failure.Message.Contains("short", StringComparison.Ordinal));
            Assert.IsFalse(failure.Message.Contains("synthetic-environment-secret", StringComparison.Ordinal));
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], originals[index]);
        }
    }

    [TestMethod]
    [DataRow("Database:PasswordFile")]
    [DataRow("OAuth:SigningKeyFile")]
    [DataRow("Mfa:EncryptionKeyFile")]
    [DataRow("Messaging:EncryptionKeyFile")]
    [DataRow("Messaging:DecryptionKeys:0:KeyFile")]
    [DataRow("ObjectStorage:ConnectionStringFile")]
    [DataRow("Tls:CertificatePath")]
    [DataRow("Tls:CertificateKeyPath")]
    [DataRow("Dkim:PrivateKeyPath")]
    [DataRow("Admin:DataProtectionKeyPath")]
    public async Task SecretFileFallbacksRefuseBeforeAnyReferencedFileIsRead(string key)
    {
        using var fixture = new SettingsFixture();
        var missing = Path.Combine(fixture.Root.FullName, "never-created-secret");
        fixture.Values[key] = missing;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LoadAsync(fixture)).ConfigureAwait(false);
        Assert.Contains("Separate secret-file settings", failure.Message, StringComparison.Ordinal);
        Assert.IsFalse(failure.Message.Contains(missing, StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(missing));
    }

    [TestMethod]
    [DataRow("OAuth:SigningKey")]
    [DataRow("Mfa:EncryptionKey")]
    [DataRow("Dkim:PrivateKeyPem")]
    public async Task GatewayRefusesWorkerOnlySecretValues(string key)
    {
        using var fixture = new SettingsFixture();
        const string secret = "synthetic-worker-only-secret";
        fixture.Values[key] = secret;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LoadAsync(fixture, EnvironmentValidationRole.Gateway)).ConfigureAwait(false);
        Assert.Contains("Worker-only OAuth, MFA or DKIM", failure.Message, StringComparison.Ordinal);
        Assert.IsFalse(failure.Message.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("UnknownSetting", "synthetic-invalid-value")]
    [DataRow("Database:Port", "synthetic-invalid-value")]
    public async Task UnknownOrUnconvertibleSettingsRefuseWithoutValueDisclosure(string key, string value)
    {
        using var fixture = new SettingsFixture();
        fixture.Values[key] = value;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LoadAsync(fixture)).ConfigureAwait(false);
        Assert.AreEqual("The portable configuration contains unknown or invalid settings.", failure.Message, StringComparer.Ordinal);
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.ToString().Contains(value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task JsonIsNotImportedOrSilentlyRecovered()
    {
        using var fixture = new SettingsFixture();
        var path = Path.Combine(fixture.Root.FullName, ".env");
        const string original = "{\"Database\":{\"Password\":\"synthetic-json-secret\"}}";
        await File.WriteAllTextAsync(path, original, TestContext.CancellationToken).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SupprocomSecretsException>(() =>
            LoadAsync(fixture)).ConfigureAwait(false);
        Assert.AreEqual(original, await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task MissingActiveDocumentDoesNotActivateATemplate()
    {
        using var fixture = new SettingsFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root.FullName, ".env.template"),
            "Database__Password=\"not-an-active-secret\"", TestContext.CancellationToken).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(fixture)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root.FullName, ".env")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root.FullName, ".installation.key")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActiveLossAfterPrecheckCannotActivateASeparateTemplate(bool truncate)
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var active = Path.Combine(fixture.Root.FullName, ".env");
        var template = Path.Combine(fixture.Root.FullName, ".env.template");
        var templateBytes = await File.ReadAllBytesAsync(active, TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(template, templateBytes, TestContext.CancellationToken).ConfigureAwait(false);
        var precheckEntries = 0;
        Task<EnvironmentConfig> AttemptAsync() => PortableEnvironmentLoader.LoadFromDirectoryAsync(
            fixture.Root.FullName, EnvironmentValidationRole.Combined, TestContext.CancellationToken,
            async token =>
            {
                Assert.IsTrue(File.Exists(active));
                Assert.IsGreaterThan(0L, new FileInfo(active).Length);
                precheckEntries++;
                if (truncate)
                    await File.WriteAllBytesAsync(active, [], token).ConfigureAwait(false);
                else
                    File.Delete(active);
            });

        if (truncate)
        {
            var failure = await Assert.ThrowsExactlyAsync<SupprocomSecretsException>(AttemptAsync).ConfigureAwait(false);
            Assert.AreEqual("InvalidTemplate", failure.Code, StringComparer.Ordinal);
            Assert.AreEqual(0L, new FileInfo(active).Length);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(AttemptAsync).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(active));
        }

        Assert.AreEqual(1, precheckEntries);
        Assert.AreSequenceEqual(templateBytes,
            await File.ReadAllBytesAsync(template, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreSequenceEqual(truncate ? TruncatedActiveFiles : RemovedActiveFiles,
            Directory.EnumerateFiles(fixture.Root.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ExternalProviderPointerIsRefusedByTheBuiltinStore()
    {
        using var fixture = new SettingsFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root.FullName, ".env"),
            "SUPPROCOM_SECRET_SOURCE=\"https://secrets.invalid/owned-fixture\"", TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<SupprocomSecretsException>(() => LoadAsync(fixture)).ConfigureAwait(false);
        Assert.AreEqual("ExternalProviderPointer", failure.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ActiveDocumentIsProtectedAndCannotMoveWithoutItsInstallationKey()
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await LoadAsync(fixture).ConfigureAwait(false);
        var file = Path.Combine(fixture.Root.FullName, ".env");
        var ciphertext = await File.ReadAllBytesAsync(file, TestContext.CancellationToken).ConfigureAwait(false);
        var options = new SupprocomSecretFileOptions { Directory = fixture.Root.FullName };
        var store = new SupprocomSecretFileStore(options, environmentName: "Production");
        Assert.AreEqual(SecretFileProtectionState.Protected,
            await store.GetStateAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root.FullName, ".installation.key")));

        using var other = new SettingsFixture();
        var otherFile = Path.Combine(other.Root.FullName, ".env");
        await File.WriteAllBytesAsync(otherFile, ciphertext, TestContext.CancellationToken).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SupprocomSecretsException>(() => LoadAsync(other)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(other.Root.FullName, ".installation.key")));
        Assert.AreSequenceEqual(ciphertext,
            await File.ReadAllBytesAsync(otherFile, TestContext.CancellationToken).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ProtectedCookieKeysRemainGatewayOnlySnapshotValues()
    {
        using var fixture = new SettingsFixture();
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var config = await LoadAsync(fixture, EnvironmentValidationRole.Gateway).ConfigureAwait(false);
        Assert.HasCount(1, config.Admin.DataProtectionKeys);
        Assert.AreEqual(fixture.CookieKey.MasterKey, config.Admin.DataProtectionKeys[0].MasterKey, StringComparer.Ordinal);
        var active = Path.Combine(fixture.Root.FullName, ".env");
        var protectedBytes = await File.ReadAllBytesAsync(active, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain(fixture.CookieKey.MasterKey, System.Text.Encoding.UTF8.GetString(protectedBytes), StringComparison.Ordinal);
        await File.WriteAllTextAsync(active, "replaced-invalid-active", TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(fixture.CookieKey.MasterKey, config.Admin.DataProtectionKeys[0].MasterKey, StringComparer.Ordinal);
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LoadAsync(fixture, EnvironmentValidationRole.ApplicationWorker)).ConfigureAwait(false);
        Assert.AreEqual("Worker configuration must not contain Gateway admin-cookie keys.", failure.Message, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ProtectedWorkerSecretsShareOneValidatedStartupSnapshot()
    {
        using var fixture = new SettingsFixture(includeCookieKeys: false);
        using var signer = System.Security.Cryptography.RSA.Create(2048);
        var signing = signer.ExportPkcs8PrivateKeyPem();
        var encryption = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var old = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        const string connection = "DefaultEndpointsProtocol=https;AccountName=fixture;AccountKey=synthetic;BlobEndpoint=https://blob.example.test/";
        fixture.Values["OAuth:EnableOAuth"] = "true";
        fixture.Values["OAuth:EnableOpenIdConnect"] = "true";
        fixture.Values["OAuth:SigningKey"] = signing;
        fixture.Values["Mfa:EnableTotp"] = "true";
        fixture.Values["Mfa:EncryptionKey"] = encryption;
        fixture.Values["Messaging:Enabled"] = "true";
        fixture.Values["Messaging:EncryptionKey"] = encryption;
        fixture.Values["Messaging:DecryptionKeys:0:Id"] = "old";
        fixture.Values["Messaging:DecryptionKeys:0:Key"] = old;
        fixture.Values["ObjectStorage:ConnectionString"] = connection;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(fixture, EnvironmentValidationRole.ApplicationWorker).ConfigureAwait(false);
        var active = Path.Combine(fixture.Root.FullName, ".env");
        var bytes = await File.ReadAllBytesAsync(active, TestContext.CancellationToken).ConfigureAwait(false);
        var ciphertext = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var secret in new[] { signing, encryption, old, connection })
            Assert.DoesNotContain(secret, ciphertext, StringComparison.Ordinal);
        await File.WriteAllTextAsync(active, "later-invalid-document", TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(signing, loaded.OAuth.SigningKey, StringComparer.Ordinal);
        Assert.AreEqual(encryption, loaded.Mfa.EncryptionKey, StringComparer.Ordinal);
        Assert.AreEqual(encryption, loaded.Messaging.EncryptionKey, StringComparer.Ordinal);
        Assert.AreEqual(old, loaded.Messaging.DecryptionKeys.Single().Key, StringComparer.Ordinal);
        Assert.AreEqual(connection, loaded.ObjectStorage.ConnectionString, StringComparer.Ordinal);
        Assert.IsEmpty(loaded.Validate(role: EnvironmentValidationRole.ApplicationWorker));
    }

    private Task<EnvironmentConfig> LoadAsync(
        SettingsFixture fixture,
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined) =>
        PortableEnvironmentLoader.LoadFromDirectoryAsync(fixture.Root.FullName, role, TestContext.CancellationToken);

    private sealed class SettingsFixture(bool includeCookieKeys = true) : IDisposable
    {
        internal DataProtectionKeyConfig CookieKey { get; } = PortableCookieKeyFixture.Create();
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("mk8-portable-settings-");
        // Reserved names and synthetic credentials are finite local parsing controls.
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
        };

        internal Task WriteAsync(CancellationToken cancellationToken)
        {
            Values["Admin:AllowedNetworks:0"] = "127.0.0.1/32";
            if (includeCookieKeys) PortableCookieKeyFixture.AddValues(Values, CookieKey);
            Values["Admin:AuditLogPath"] = Path.Combine(Root.FullName, "audit.jsonl");
            Values["Admin:HealthStatusPath"] = Path.Combine(Root.FullName, "health.json");
            var text = SupprocomSecretDocument.Serialize(
                Values.Select(pair => new SupprocomSecretSetting(pair.Key, pair.Value)));
            return File.WriteAllTextAsync(Path.Combine(Root.FullName, ".env"), text, cancellationToken);
        }

        public void Dispose()
        {
            Root.Delete(recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
