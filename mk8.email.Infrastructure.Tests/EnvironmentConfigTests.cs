using System.Text.Json;
using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class EnvironmentConfigTests
{
    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = WriteFile("certificate.pem", "test certificate");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    public void ValidProductionConfigurationHasNoErrors()
    {
        var errors = CreateValidConfiguration().Validate();

        Assert.AreEqual(0, errors.Count, string.Join(System.Environment.NewLine, errors));
    }

    [TestMethod]
    public void ProductionSubmissionRequiresStartTls()
    {
        var errors = CreateValidConfiguration(enableStartTls: false, enableImap: false).Validate();

        StringAssert.Contains(string.Join('|', errors), "SMTP submission requires STARTTLS.");
    }

    [TestMethod]
    public void ProductionPop3RequiresStls()
    {
        var errors = CreateValidConfiguration(
            enablePop3: true,
            enablePop3StartTls: false).Validate();

        StringAssert.Contains(string.Join('|', errors), "The production POP3 listener requires STLS.");
    }

    [TestMethod]
    public void ProductionManageSieveRequiresStartTls()
    {
        var errors = CreateValidConfiguration(
            enableSieve: true,
            enableSieveStartTls: false).Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "The ManageSieve listener requires STARTTLS.");
    }

    [TestMethod]
    public void ManageSieveQuotaAndListenerPortAreValidated()
    {
        var errors = CreateValidConfiguration(
            enableSieve: true,
            sievePort: 2525,
            sieveMaxScripts: 0).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "Enabled listeners cannot share port 2525.");
        StringAssert.Contains(joined, "Sieve.MaxScriptsPerUser must be from 1 through 1000.");
    }

    [TestMethod]
    public void ProductionRejectsSimplifiedInboundAuthenticationChecks()
    {
        var errors = CreateValidConfiguration(enableSpfCheck: true).Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "The built-in SPF and DMARC checks are not approved for production.");
    }

    [TestMethod]
    public void ProductionDkimSigningRejectsInvalidSelector()
    {
        var errors = CreateValidConfiguration(enableDkimSigning: true, dkimSelector: "-invalid").Validate();

        StringAssert.Contains(string.Join('|', errors), "Dkim.Selector must be a DNS label.");
    }

    [TestMethod]
    public void DkimIdentityValidationRejectsNullInputs()
    {
        Assert.IsFalse(DkimIdentityValidator.IsValidDomain(null!));
        Assert.IsFalse(DkimIdentityValidator.IsValidSelector(null!));
    }

    [TestMethod]
    public void ProductionRequiresLoopbackRspamdEndpoint()
    {
        var errors = CreateValidConfiguration(rspamdEndpoint: "http://scanner.example/checkv2").Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "Filtering.RspamdEndpoint must use a loopback address in production.");
    }

    [TestMethod]
    public void QueueAttemptLimitMustBeBounded()
    {
        var errors = CreateValidConfiguration(queueMaxAttempts: 0).Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "Queue.MaxAttempts must be from 1 through 100.");
    }

    [TestMethod]
    public void DavResourceAndCollectionLimitsMustBeBounded()
    {
        var errors = CreateValidConfiguration(
            davMaxResourceSizeBytes: 1024,
            davMaxCollectionsPerUser: 0,
            davMaxResourcesPerCollection: 1_000_001).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(
            joined,
            "Dav.MaxResourceSizeBytes must be from 65536 through 1073741824.");
        StringAssert.Contains(
            joined,
            "Dav.MaxCollectionsPerUser must be from 1 through 1000.");
        StringAssert.Contains(
            joined,
            "Dav.MaxResourcesPerCollection must be from 1 through 1000000.");
    }

    [TestMethod]
    public void OAuthTokenLifetimesMustBeBounded()
    {
        var errors = CreateValidConfiguration(
            oauthAccessTokenMinutes: 0,
            oauthRefreshTokenDays: 366).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "OAuth.AccessTokenMinutes must be from 1 through 60.");
        StringAssert.Contains(joined, "OAuth.RefreshTokenDays must be from 1 through 365.");
    }

    [TestMethod]
    public void EnabledOAuthRequiresBoundedCodesAndSecurePublicMetadata()
    {
        var configuration = CreateValidConfiguration(
            oauthEnable: true,
            oauthPublicBaseUrl: "http://email.mk8n.com?unsafe=true",
            oauthClientId: "",
            oauthAuthorizationCodeMinutes: 16);

        var joined = string.Join('|', configuration.Validate());
        StringAssert.Contains(joined, "OAuth.AuthorizationCodeMinutes must be from 1 through 15.");
        StringAssert.Contains(joined, "OAuth.ClientId must contain from 1 through 128 visible ASCII characters.");
        StringAssert.Contains(joined, "OAuth.PublicBaseUrl must be an absolute HTTPS URL without a query or fragment.");
    }

    [TestMethod]
    public void TotpMfaRequiresOAuthAndAValidEncryptionKey()
    {
        var errors = CreateValidConfiguration(
            mfaEnable: true,
            mfaEncryptionKey: "not-base64",
            mfaIssuer: "",
            mfaRecoveryCodeCount: 4).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "Mfa.EnableTotp requires OAuth.EnableOAuth.");
        StringAssert.Contains(joined, "Mfa.Issuer must contain from 1 through 128 non-control characters.");
        StringAssert.Contains(joined, "Mfa.EncryptionKey must be a base64-encoded 256-bit key.");
        StringAssert.Contains(joined, "Mfa.RecoveryCodeCount must be from 5 through 20.");
    }

    [TestMethod]
    public void OpenIdConnectRequiresOAuthAndAValidSigningKey()
    {
        var errors = CreateValidConfiguration(
            oauthPublicBaseUrl: "https://email.mk8n.com/tenant",
            oidcEnable: true,
            oidcSigningKey: "not-a-private-key",
            oidcIdTokenMinutes: 0).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "OAuth.EnableOpenIdConnect requires OAuth.EnableOAuth.");
        StringAssert.Contains(joined, "OAuth.SigningKey must be an RSA private key of at least 2048 bits.");
        StringAssert.Contains(joined, "OAuth.IdTokenMinutes must be from 1 through 60.");
        StringAssert.Contains(joined, "OAuth.PublicBaseUrl must not contain a path when OpenID Connect is enabled.");
    }

    [TestMethod]
    public void LoaderReadsPasswordsFromSecretFiles()
    {
        var databasePasswordPath = WriteFile("database-password", "database-secret-value");
        var configuration = CreateValidConfiguration(databasePasswordPath);
        var configurationPath = WriteFile(
            "mk8email.config.json",
            JsonSerializer.Serialize(configuration));

        var loaded = EnvironmentLoader.LoadFromFile(configurationPath);

        Assert.AreEqual("database-secret-value", loaded.Database.Password);
    }

    [TestMethod]
    public void LoaderReadsMfaEncryptionKeyFromSecretFile()
    {
        var encodedKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var keyPath = WriteFile("mfa-encryption-key", encodedKey);
        var configuration = CreateValidConfiguration(
            oauthEnable: true,
            mfaEnable: true,
            mfaEncryptionKeyFile: keyPath);
        var configurationPath = WriteFile(
            "mk8email-mfa.config.json",
            JsonSerializer.Serialize(configuration));

        var loaded = EnvironmentLoader.LoadFromFile(configurationPath);

        Assert.AreEqual(encodedKey, loaded.Mfa.EncryptionKey);
    }

    [TestMethod]
    public void LoaderReadsOpenIdConnectSigningKeyFromSecretFile()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var signingKey = rsa.ExportPkcs8PrivateKeyPem();
        var keyPath = WriteFile("oidc-signing-key.pem", signingKey);
        var configuration = CreateValidConfiguration(
            oauthEnable: true,
            oidcEnable: true,
            oidcSigningKeyFile: keyPath);
        var configurationPath = WriteFile(
            "mk8email-oidc.config.json",
            JsonSerializer.Serialize(configuration));

        var loaded = EnvironmentLoader.LoadFromFile(configurationPath);

        Assert.AreEqual(signingKey.TrimEnd('\r', '\n'), loaded.OAuth.SigningKey);
    }

    [TestMethod]
    public void GatewayLoaderDoesNotReadApplicationOnlyOAuthOrMfaSecrets()
    {
        var configuration = CreateValidConfiguration(
            oauthEnable: true,
            oauthPublicBaseUrl: "https://email.mk8n.com",
            oidcEnable: true,
            oidcSigningKeyFile: Path.Combine(_testDirectory, "worker-only-signing-key"),
            mfaEnable: true,
            mfaEncryptionKeyFile: Path.Combine(_testDirectory, "worker-only-mfa-key"));
        var configurationPath = WriteFile(
            "mk8email-gateway.config.json",
            JsonSerializer.Serialize(configuration));

        var loaded = EnvironmentLoader.LoadFromFile(
            configurationPath,
            role: EnvironmentValidationRole.Gateway);

        Assert.AreEqual(string.Empty, loaded.OAuth.SigningKey);
        Assert.AreEqual(string.Empty, loaded.Mfa.EncryptionKey);
    }

    [TestMethod]
    public void GatewayDoesNotRequireApplicationOnlyDkimPrivateKey()
    {
        var missingWorkerKey = Path.Combine(_testDirectory, "worker-only-dkim-key");
        var configuration = CreateValidConfiguration(
            enableDkimSigning: true,
            dkimPrivateKeyPath: missingWorkerKey);
        var configurationPath = WriteFile(
            "mk8email-gateway-with-dkim.config.json",
            JsonSerializer.Serialize(configuration));

        var gateway = EnvironmentLoader.LoadFromFile(
            configurationPath,
            role: EnvironmentValidationRole.Gateway);
        Assert.AreEqual(0, gateway.Validate(role: EnvironmentValidationRole.Gateway).Count);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            EnvironmentLoader.LoadFromFile(
                configurationPath,
                role: EnvironmentValidationRole.ApplicationWorker));
        StringAssert.Contains(exception.Message, "Dkim.PrivateKeyPath");
    }

    [TestMethod]
    public void DistributedMessagingRequiresAzureBlobAndValidEncryptionSettings()
    {
        var configuration = CreateValidConfiguration(
            messagingEnabled: true,
            messagingEncryptionKey: "not-base64",
            objectStorageProvider: "filesystem",
            objectStorageConnectionString: "short",
            objectStorageContainerName: "Invalid--Container");

        var joined = string.Join('|', configuration.Validate());
        StringAssert.Contains(joined, "Messaging.EncryptionKey must be a base64-encoded 256-bit key.");
        StringAssert.Contains(joined, "ObjectStorage.Provider must be azure-blob.");
        StringAssert.Contains(joined, "ObjectStorage.ConnectionString must contain at least 16 characters");
        StringAssert.Contains(joined, "ObjectStorage.ContainerName is not a valid Azure Blob container name.");
    }

    [TestMethod]
    public void DistributedMessagingPayloadMustFitAdvertisedAppendLimit()
    {
        var key = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var connectionString =
            "DefaultEndpointsProtocol=https;AccountName=mk8;AccountKey=test;"
            + "BlobEndpoint=https://blob.example.test/;";
        var insufficient = CreateValidConfiguration(
            messagingEnabled: true,
            messagingEncryptionKey: key,
            objectStorageConnectionString: connectionString,
            messagingMaxPayloadBytes: 32 * 1024 * 1024);
        StringAssert.Contains(
            string.Join('|', insufficient.Validate()),
            "Messaging.MaxPayloadBytes must accommodate base64-encoded "
            + "Limits.MaxMessageSizeBytes plus IMAP APPEND request overhead.");

        var sufficient = CreateValidConfiguration(
            messagingEnabled: true,
            messagingEncryptionKey: key,
            objectStorageConnectionString: connectionString,
            messagingMaxPayloadBytes: 64 * 1024 * 1024);
        Assert.AreEqual(0, sufficient.Validate().Count);
    }

    [TestMethod]
    public void LoaderReadsDistributedMessagingSecretsFromFiles()
    {
        var activeKey = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var oldKey = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var connectionString =
            "DefaultEndpointsProtocol=https;AccountName=mk8;AccountKey=test;"
            + "BlobEndpoint=https://blob.example.test/;";
        var activePath = WriteFile("messaging-active-key", activeKey);
        var oldPath = WriteFile("messaging-old-key", oldKey);
        var connectionPath = WriteFile("blob-connection", connectionString);
        var configuration = CreateValidConfiguration(
            messagingEnabled: true,
            messagingEncryptionKeyFile: activePath,
            messagingDecryptionKeys:
            [
                new MessagingDecryptionKeyConfig { Id = "old", KeyFile = oldPath },
            ],
            objectStorageConnectionStringFile: connectionPath);
        var configurationPath = WriteFile(
            "mk8email-messaging.config.json",
            JsonSerializer.Serialize(configuration));

        var loaded = EnvironmentLoader.LoadFromFile(configurationPath);
        var gateway = EnvironmentLoader.LoadFromFile(
            configurationPath, role: EnvironmentValidationRole.Gateway);
        var worker = EnvironmentLoader.LoadFromFile(
            configurationPath, role: EnvironmentValidationRole.ApplicationWorker);

        Assert.AreEqual(activeKey, loaded.Messaging.EncryptionKey);
        Assert.AreEqual(oldKey, loaded.Messaging.DecryptionKeys.Single().Key);
        Assert.AreEqual(connectionString, loaded.ObjectStorage.ConnectionString);
        Assert.AreEqual(0, loaded.Validate().Count);
        Assert.AreEqual(activeKey, gateway.Messaging.EncryptionKey);
        Assert.AreEqual(connectionString, gateway.ObjectStorage.ConnectionString);
        Assert.AreEqual(0, gateway.Validate(role: EnvironmentValidationRole.Gateway).Count);
        Assert.AreEqual(activeKey, worker.Messaging.EncryptionKey);
        Assert.AreEqual(connectionString, worker.ObjectStorage.ConnectionString);
        Assert.AreEqual(0, worker.Validate(role: EnvironmentValidationRole.ApplicationWorker).Count);
    }

    [TestMethod]
    public void ApplicationWorkerValidationDoesNotRequirePresentationListenersOrCertificates()
    {
        var configuration = new EnvironmentConfig
        {
            Database = new DatabaseConfig
            {
                Host = "database.internal",
                Name = "mk8email",
                Username = "application-worker",
                Password = "database-secret-value",
            },
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
                EnableSmtp = false,
                EnableSubmission = false,
                EnableImplicitTls = false,
            },
            Imap = new ImapConfig { EnableImap = false, EnableImplicitTls = false },
            Pop3 = new Pop3Config { EnablePop3 = false, EnableImplicitTls = false },
            Sieve = new SieveConfig { EnableManageSieve = false },
            Jmap = new JmapConfig { EnableJmap = false, IsDefault = false },
            Dav = new DavConfig { EnableDav = false },
            OAuth = new OAuthConfig { EnableOAuth = false },
            Messaging = new MessagingConfig
            {
                Enabled = true,
                EncryptionKeyId = "current",
                EncryptionKey = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            },
            ObjectStorage = new ObjectStorageConfig
            {
                ConnectionString =
                    "DefaultEndpointsProtocol=https;AccountName=mk8;AccountKey=test;"
                    + "BlobEndpoint=https://blob.example.test/;",
            },
        };

        var workerErrors = configuration.Validate(
            role: EnvironmentValidationRole.ApplicationWorker);
        var combinedErrors = configuration.Validate();

        Assert.AreEqual(0, workerErrors.Count, string.Join('|', workerErrors));
        StringAssert.Contains(
            string.Join('|', combinedErrors),
            "Enable at least one SMTP, IMAP, POP3, ManageSieve, JMAP, DAV, or OAuth listener.");
    }

    [TestMethod]
    public void LoaderRejectsUnknownConfigurationProperties()
    {
        var configurationPath = WriteFile("invalid.json", "{\"UnknownProperty\":true}");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => EnvironmentLoader.LoadFromFile(configurationPath));

        StringAssert.Contains(exception.Message, "The configuration file is not valid JSON");
    }

    private EnvironmentConfig CreateValidConfiguration(
        string? databasePasswordFile = null,
        bool enableStartTls = true,
        bool enableImap = true,
        bool enableSpfCheck = false,
        bool enableDkimSigning = false,
        string? dkimPrivateKeyPath = null,
        string dkimSelector = "default",
        string rspamdEndpoint = "http://127.0.0.1:11333/checkv2",
        int queueMaxAttempts = 20,
        bool enablePop3 = false,
        bool enablePop3StartTls = true,
        bool enableSieve = false,
        bool enableSieveStartTls = true,
        int sievePort = 4190,
        int sieveMaxScripts = 64,
        int davMaxResourceSizeBytes = 10 * 1024 * 1024,
        int davMaxCollectionsPerUser = 100,
        int davMaxResourcesPerCollection = 100_000,
        int oauthAccessTokenMinutes = 10,
        int oauthRefreshTokenDays = 90,
        bool oauthEnable = false,
        string? oauthPublicBaseUrl = null,
        string oauthClientId = "thunderbird",
        int oauthAuthorizationCodeMinutes = 5,
        bool oidcEnable = false,
        string oidcSigningKey = "",
        string? oidcSigningKeyFile = null,
        int oidcIdTokenMinutes = 10,
        bool mfaEnable = false,
        string mfaEncryptionKey = "",
        string? mfaEncryptionKeyFile = null,
        string mfaIssuer = "mk8.email",
        int mfaRecoveryCodeCount = 10,
        bool messagingEnabled = false,
        string messagingEncryptionKey = "",
        string? messagingEncryptionKeyFile = null,
        int messagingMaxPayloadBytes = 64 * 1024 * 1024,
        IReadOnlyList<MessagingDecryptionKeyConfig>? messagingDecryptionKeys = null,
        string objectStorageProvider = "azure-blob",
        string objectStorageConnectionString = "",
        string? objectStorageConnectionStringFile = null,
        string objectStorageContainerName = "mk8-email-objects")
    {
        return new EnvironmentConfig
        {
            Database = new DatabaseConfig
            {
                Host = "database",
                Port = 5432,
                Name = "mk8email",
                Username = "mk8email",
                Password = databasePasswordFile is null ? "database-secret-value" : string.Empty,
                PasswordFile = databasePasswordFile,
            },
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
                Port = 2525,
                SubmissionPort = 2587,
                ImplicitTlsPort = 2465,
                EnableSmtp = true,
                EnableSubmission = true,
                EnableImplicitTls = true,
                EnableStartTls = enableStartTls,
                RequireTls = false,
                RequireAuth = true,
                AllowRelay = true,
            },
            Imap = new ImapConfig
            {
                Port = 2143,
                ImplicitTlsPort = 2993,
                EnableImap = enableImap,
                EnableImplicitTls = true,
            },
            Pop3 = new Pop3Config
            {
                Port = 2110,
                ImplicitTlsPort = 2995,
                EnablePop3 = enablePop3,
                EnableImplicitTls = false,
                EnableStartTls = enablePop3StartTls,
            },
            Sieve = new SieveConfig
            {
                Port = sievePort,
                EnableManageSieve = enableSieve,
                EnableStartTls = enableSieveStartTls,
                MaxScriptsPerUser = sieveMaxScripts,
            },
            Dav = new DavConfig
            {
                EnableDav = true,
                MaxResourceSizeBytes = davMaxResourceSizeBytes,
                MaxCollectionsPerUser = davMaxCollectionsPerUser,
                MaxResourcesPerCollection = davMaxResourcesPerCollection,
            },
            OAuth = new OAuthConfig
            {
                EnableOAuth = oauthEnable,
                EnableOpenIdConnect = oidcEnable,
                PublicBaseUrl = oauthPublicBaseUrl,
                ClientId = oauthClientId,
                AccessTokenMinutes = oauthAccessTokenMinutes,
                RefreshTokenDays = oauthRefreshTokenDays,
                AuthorizationCodeMinutes = oauthAuthorizationCodeMinutes,
                IdTokenMinutes = oidcIdTokenMinutes,
                SigningKey = oidcSigningKey,
                SigningKeyFile = oidcSigningKeyFile,
            },
            Mfa = new MfaConfig
            {
                EnableTotp = mfaEnable,
                EncryptionKey = mfaEncryptionKey,
                EncryptionKeyFile = mfaEncryptionKeyFile,
                Issuer = mfaIssuer,
                RecoveryCodeCount = mfaRecoveryCodeCount,
            },
            Tls = new TlsConfig
            {
                CertificatePath = _certificatePath,
            },
            Dkim = new DkimConfig
            {
                PrivateKeyPath = dkimPrivateKeyPath ?? (enableDkimSigning ? _certificatePath : null),
                Selector = dkimSelector,
                EnableSigning = enableDkimSigning,
            },
            Security = new SecurityConfig
            {
                EnableSpfCheck = enableSpfCheck,
                EnableDmarcCheck = false,
                PasswordHashScheme = "BLF-CRYPT",
            },
            Filtering = new FilteringConfig
            {
                RspamdEndpoint = rspamdEndpoint,
                TimeoutSeconds = 70,
            },
            Queue = new QueueConfig
            {
                PollIntervalMilliseconds = 500,
                LeaseSeconds = 300,
                MaxAttempts = queueMaxAttempts,
                MaxAgeHours = 120,
                CompletedRetentionDays = 14,
            },
            Limits = new LimitsConfig
            {
                MaxMessageSizeBytes = 25 * 1024 * 1024,
                MaxRecipientsPerMessage = 100,
                ConnectionTimeoutSeconds = 300,
                MaxConnectionsPerIp = 20,
            },
            General = new GeneralConfig
            {
                AllowRegistration = false,
            },
            Admin = new AdminConfig
            {
                AllowedNetworks = ["127.0.0.0/8"],
                DataProtectionKeyPath = Path.Combine(_testDirectory, "data-protection"),
                AuditLogPath = Path.Combine(_testDirectory, "audit", "admin.jsonl"),
                HealthStatusPath = Path.Combine(_testDirectory, "health", "status.json"),
                SessionMinutes = 30,
            },
            Messaging = new MessagingConfig
            {
                Enabled = messagingEnabled,
                EncryptionKeyId = "current",
                EncryptionKey = messagingEncryptionKey,
                EncryptionKeyFile = messagingEncryptionKeyFile,
                MaxPayloadBytes = messagingMaxPayloadBytes,
                DecryptionKeys = messagingDecryptionKeys ?? [],
            },
            ObjectStorage = new ObjectStorageConfig
            {
                Provider = objectStorageProvider,
                ConnectionString = objectStorageConnectionString,
                ConnectionStringFile = objectStorageConnectionStringFile,
                ContainerName = objectStorageContainerName,
            },
        };
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
