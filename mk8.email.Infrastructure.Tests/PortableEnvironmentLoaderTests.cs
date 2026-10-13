using mk8.email.Configuration;
using Supprocom.Secrets;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1515", Justification = "MSTest requires a public fixture for discovery.")]
public sealed class PortableEnvironmentLoaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(EnvironmentValidationRole.Gateway)]
    [DataRow(EnvironmentValidationRole.ApplicationWorker)]
    public async Task PortableValuesAreValidatedAndRemainAStartupSnapshot(EnvironmentValidationRole role)
    {
        using var fixture = new SettingsFixture();
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
    public async Task GatewayRefusesWorkerOnlySecretValues(string key)
    {
        using var fixture = new SettingsFixture();
        const string secret = "synthetic-worker-only-secret";
        fixture.Values[key] = secret;
        await fixture.WriteAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LoadAsync(fixture, EnvironmentValidationRole.Gateway)).ConfigureAwait(false);
        Assert.Contains("Worker-only OAuth or MFA", failure.Message, StringComparison.Ordinal);
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

    private Task<EnvironmentConfig> LoadAsync(
        SettingsFixture fixture,
        EnvironmentValidationRole role = EnvironmentValidationRole.Combined) =>
        PortableEnvironmentLoader.LoadFromDirectoryAsync(fixture.Root.FullName, role, TestContext.CancellationToken);

    private sealed class SettingsFixture : IDisposable
    {
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
            Values["Admin:DataProtectionKeyPath"] = Path.Combine(Root.FullName, "data-protection");
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
