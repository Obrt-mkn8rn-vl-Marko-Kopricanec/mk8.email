using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using mk8.email.Configuration;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class EnvironmentConfigTests
{
    private string _testDirectory = null!;
    private string _certificatePkcs12 = null!;
    private string _dkimPrivateKeyPem = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        _certificatePkcs12 = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12));
        _dkimPrivateKeyPem = key.ExportPkcs8PrivateKeyPem();
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
    [DataRow(EnvironmentValidationRole.Gateway)]
    [DataRow(EnvironmentValidationRole.ApplicationWorker)]
    public void EwsSharedReadLimitsRemainValidatedWithJmapHttpDisabled(EnvironmentValidationRole role)
    {
        var configuration = new EnvironmentConfig
        {
            Jmap = new JmapConfig { EnableJmap = false, IsDefault = false, MaxObjectsInGet = 0, MaxObjectsInSet = 0, MaxConcurrentRequests = 0 },
        };
        var errors = configuration.Validate(isDevelopment: true, role: role);
        StringAssert.Contains(string.Join('|', errors), "Jmap.MaxObjectsInGet must be from 1 through 10000.", StringComparison.Ordinal);
        StringAssert.Contains(string.Join('|', errors), "Jmap.MaxObjectsInSet must be from 1 through 10000.", StringComparison.Ordinal);
        StringAssert.Contains(string.Join('|', errors), "Jmap.MaxConcurrentRequests must be from 1 through 1024.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductionSubmissionRequiresStartTls()
    {
        var errors = CreateValidConfiguration(enableStartTls: false, enableImap: false).Validate();

        StringAssert.Contains(string.Join('|', errors), "SMTP submission requires STARTTLS.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductionPop3RequiresStls()
    {
        var errors = CreateValidConfiguration(
            enablePop3: true,
            enablePop3StartTls: false).Validate();

        StringAssert.Contains(string.Join('|', errors), "The production POP3 listener requires STLS.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductionManageSieveRequiresStartTls()
    {
        var errors = CreateValidConfiguration(
            enableSieve: true,
            enableSieveStartTls: false).Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "The ManageSieve listener requires STARTTLS.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ManageSieveQuotaAndListenerPortAreValidated()
    {
        var errors = CreateValidConfiguration(
            enableSieve: true,
            sievePort: 2525,
            sieveMaxScripts: 0).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "Enabled listeners cannot share port 2525.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "Sieve.MaxScriptsPerUser must be from 1 through 1000.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductionRejectsSimplifiedInboundAuthenticationChecks()
    {
        var errors = CreateValidConfiguration(enableSpfCheck: true).Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "The built-in SPF and DMARC checks are not approved for production.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductionDkimSigningRejectsInvalidSelector()
    {
        var errors = CreateValidConfiguration(enableDkimSigning: true, dkimSelector: "-invalid").Validate();

        StringAssert.Contains(string.Join('|', errors), "Dkim.Selector must be a DNS label.", StringComparison.Ordinal);
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
            "Filtering.RspamdEndpoint must use a loopback address in production.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void QueueAttemptLimitMustBeBounded()
    {
        var errors = CreateValidConfiguration(queueMaxAttempts: 0).Validate();

        StringAssert.Contains(
            string.Join('|', errors),
            "Queue.MaxAttempts must be from 1 through 100.", StringComparison.Ordinal);
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
            "Dav.MaxResourceSizeBytes must be from 65536 through 1073741824.", StringComparison.Ordinal);
        StringAssert.Contains(
            joined,
            "Dav.MaxCollectionsPerUser must be from 1 through 1000.", StringComparison.Ordinal);
        StringAssert.Contains(
            joined,
            "Dav.MaxResourcesPerCollection must be from 1 through 1000000.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void OAuthTokenLifetimesMustBeBounded()
    {
        var errors = CreateValidConfiguration(
            oauthAccessTokenMinutes: 0,
            oauthRefreshTokenDays: 366).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "OAuth.AccessTokenMinutes must be from 1 through 60.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "OAuth.RefreshTokenDays must be from 1 through 365.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EnabledOAuthRequiresBoundedCodesAndSecurePublicMetadata()
    {
        var configuration = CreateValidConfiguration(
            oauthEnable: true,
            oauthPublicBaseUrl: "http://email.tenant.example.test?unsafe=true",
            oauthClientId: "",
            oauthAuthorizationCodeMinutes: 16);

        var joined = string.Join('|', configuration.Validate());
        StringAssert.Contains(joined, "OAuth.AuthorizationCodeMinutes must be from 1 through 15.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "OAuth.ClientId must contain from 1 through 128 visible ASCII characters.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "OAuth.PublicBaseUrl must be an absolute HTTPS URL without a query or fragment.", StringComparison.Ordinal);
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
        StringAssert.Contains(joined, "Mfa.EnableTotp requires OAuth.EnableOAuth.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "Mfa.Issuer must contain from 1 through 128 non-control characters.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "Mfa.EncryptionKey must be a base64-encoded 256-bit key.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "Mfa.RecoveryCodeCount must be from 5 through 20.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void OpenIdConnectRequiresOAuthAndAValidSigningKey()
    {
        var errors = CreateValidConfiguration(
            oauthPublicBaseUrl: "https://email.tenant.example.test/tenant",
            oidcEnable: true,
            oidcSigningKey: "not-a-private-key",
            oidcIdTokenMinutes: 0).Validate();

        var joined = string.Join('|', errors);
        StringAssert.Contains(joined, "OAuth.EnableOpenIdConnect requires OAuth.EnableOAuth.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "OAuth.SigningKey must be an RSA private key of at least 2048 bits.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "OAuth.IdTokenMinutes must be from 1 through 60.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "OAuth.PublicBaseUrl must not contain a path when OpenID Connect is enabled.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RetiredLoaderRefusesDatabaseSecretFiles()
    {
        var secret = WriteFile("database-password", "synthetic-retired-database-password");
        var path = WriteFile("retired-database.json", JsonSerializer.Serialize(CreateValidConfiguration(secret)));
        AssertLegacyRefusal(path);
        Assert.AreEqual("synthetic-retired-database-password", File.ReadAllText(secret), StringComparer.Ordinal);
    }

    [TestMethod]
    public void RetiredLoaderRefusesMfaSecretFiles()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secret = WriteFile("retired-mfa-key", value);
        var path = WriteFile("retired-mfa.json", JsonSerializer.Serialize(
            CreateValidConfiguration(oauthEnable: true, mfaEnable: true, mfaEncryptionKeyFile: secret)));
        AssertLegacyRefusal(path);
        Assert.AreEqual(value, File.ReadAllText(secret), StringComparer.Ordinal);
    }

    [TestMethod]
    public void RetiredLoaderRefusesOpenIdSigningKeyFiles()
    {
        using var signer = RSA.Create(2048);
        var value = signer.ExportPkcs8PrivateKeyPem();
        var secret = WriteFile("retired-signing-key.pem", value);
        var path = WriteFile("retired-oidc.json", JsonSerializer.Serialize(
            CreateValidConfiguration(oauthEnable: true, oidcEnable: true, oidcSigningKeyFile: secret)));
        AssertLegacyRefusal(path);
        Assert.AreEqual(value, File.ReadAllText(secret), StringComparer.Ordinal);
    }

    [TestMethod]
    public void RetiredGatewayLoaderRefusesWorkerSecretLocators()
    {
        var signing = Path.Combine(_testDirectory, "never-created-worker-signing-key");
        var mfa = Path.Combine(_testDirectory, "never-created-worker-mfa-key");
        var path = WriteFile("retired-gateway.json", JsonSerializer.Serialize(CreateValidConfiguration(
            oauthEnable: true, oidcEnable: true, oidcSigningKeyFile: signing,
            mfaEnable: true, mfaEncryptionKeyFile: mfa)));
        AssertLegacyRefusal(path, EnvironmentValidationRole.Gateway);
        Assert.IsFalse(File.Exists(signing));
        Assert.IsFalse(File.Exists(mfa));
    }

    [TestMethod]
    public void RetiredLoaderCannotAdmitEmbeddedWorkerSecrets()
    {
        var path = WriteFile("retired-embedded.json", JsonSerializer.Serialize(CreateValidConfiguration(
            oauthEnable: true, oidcEnable: true, oidcSigningKey: "synthetic-embedded-signing-secret",
            mfaEnable: true, mfaEncryptionKey: "synthetic-embedded-mfa-secret")));
        AssertLegacyRefusal(path, EnvironmentValidationRole.Gateway);
        AssertLegacyRefusal(path, EnvironmentValidationRole.ApplicationWorker);
    }

    [TestMethod]
    public void GatewayDoesNotRequireApplicationOnlyDkimPrivateKey()
    {
        var configuration = CreateValidConfiguration(enableDkimSigning: true, dkimPrivateKeyPem: string.Empty);
        Assert.IsEmpty(configuration.Validate(role: EnvironmentValidationRole.Gateway));
        Assert.Contains("Dkim.PrivateKeyPem",
            string.Join(";", configuration.Validate(role: EnvironmentValidationRole.ApplicationWorker)), StringComparison.Ordinal);
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
        StringAssert.Contains(joined, "Messaging.EncryptionKey must be a base64-encoded 256-bit key.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "ObjectStorage.Provider must be azure-blob.", StringComparison.Ordinal);
        StringAssert.Contains(joined, "ObjectStorage.ConnectionString must contain at least 16 characters", StringComparison.Ordinal);
        StringAssert.Contains(joined, "ObjectStorage.ContainerName is not a valid Azure Blob container name.", StringComparison.Ordinal);
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
            + "Limits.MaxMessageSizeBytes plus IMAP APPEND request overhead.", StringComparison.Ordinal);

        var sufficient = CreateValidConfiguration(
            messagingEnabled: true,
            messagingEncryptionKey: key,
            objectStorageConnectionString: connectionString,
            messagingMaxPayloadBytes: 64 * 1024 * 1024);
        Assert.AreEqual(0, sufficient.Validate().Count);
    }

    [TestMethod]
    [DataRow(EnvironmentValidationRole.Gateway)]
    [DataRow(EnvironmentValidationRole.ApplicationWorker)]
    public void DistributedHttpLimitsMustFitEncodedPayloadInBothRoles(EnvironmentValidationRole role)
    {
        var configuration = CreateValidConfiguration(messagingEnabled: true,
            messagingEncryptionKey: Convert.ToBase64String(new byte[32]),
            objectStorageConnectionString: "DefaultEndpointsProtocol=https;AccountName=mk8;AccountKey=test;BlobEndpoint=https://blob.example.test/;",
            jmap: new JmapConfig { MaxUploadSizeBytes = 80_000_000, MaxUnreferencedBlobBytesPerAccount = 100_000_000 });
        StringAssert.Contains(string.Join('|', configuration.Validate(role: role)),
            "base64-encoded Jmap.MaxUploadSizeBytes", StringComparison.Ordinal);
        var requestConfiguration = CreateValidConfiguration(messagingEnabled: true,
            messagingEncryptionKey: Convert.ToBase64String(new byte[32]),
            objectStorageConnectionString: "DefaultEndpointsProtocol=https;AccountName=mk8;AccountKey=test;BlobEndpoint=https://blob.example.test/;",
            jmap: new JmapConfig { MaxRequestSizeBytes = 20_000_000 }, davMaxResourceSizeBytes: 20_000_000);
        var errors = string.Join('|', requestConfiguration.Validate(role: role));
        StringAssert.Contains(errors, "JSON-escaped Jmap.MaxRequestSizeBytes", StringComparison.Ordinal);
        StringAssert.Contains(errors, "encoded Dav.MaxResourceSizeBytes", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RetiredLoaderRefusesDistributedSecretFilesForEveryRole()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var active = WriteFile("retired-active-key", key);
        var old = WriteFile("retired-old-key", key);
        var connection = WriteFile("retired-blob-connection", "synthetic-retired-connection");
        var path = WriteFile("retired-messaging.json", JsonSerializer.Serialize(CreateValidConfiguration(
            messagingEnabled: true, messagingEncryptionKeyFile: active,
            messagingDecryptionKeys: [new MessagingDecryptionKeyConfig { Id = "old", KeyFile = old }],
            objectStorageConnectionStringFile: connection)));
        foreach (var role in Enum.GetValues<EnvironmentValidationRole>()) AssertLegacyRefusal(path, role);
        Assert.AreEqual(key, File.ReadAllText(active), StringComparer.Ordinal);
        Assert.AreEqual(key, File.ReadAllText(old), StringComparer.Ordinal);
        Assert.AreEqual("synthetic-retired-connection", File.ReadAllText(connection), StringComparer.Ordinal);
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
                Hostname = "email.tenant.example.test",
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
            "Enable at least one SMTP, IMAP, POP3, ManageSieve, JMAP, DAV, or OAuth listener.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RetiredLoaderRefusesUnknownJsonWithoutParsing()
    {
        var path = WriteFile("retired-unknown.json", "{\"UnknownProperty\":true}");
        AssertLegacyRefusal(path);
    }

    private static void AssertLegacyRefusal(string path, EnvironmentValidationRole role = EnvironmentValidationRole.Combined)
    {
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => EnvironmentLoader.LoadFromFile(path, role: role));
        Assert.AreEqual("Legacy JSON/environment configuration is retired; await PortableEnvironmentLoader at the fixed installation root.", failure.Message, StringComparer.Ordinal);
        Assert.IsNull(failure.InnerException);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability", "MA0051",
        Justification = "The complete valid configuration is a single test fixture initializer.")]
    private EnvironmentConfig CreateValidConfiguration(
        string? databasePasswordFile = null,
        bool enableStartTls = true,
        bool enableImap = true,
        bool enableSpfCheck = false,
        bool enableDkimSigning = false,
        string? dkimPrivateKeyPem = null,
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
        string objectStorageContainerName = "mk8-email-objects",
        JmapConfig? jmap = null)
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
                Hostname = "email.tenant.example.test",
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
            Jmap = jmap ?? new JmapConfig(),
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
                CertificatePkcs12 = _certificatePkcs12,
            },
            Dkim = new DkimConfig
            {
                PrivateKeyPem = dkimPrivateKeyPem ?? (enableDkimSigning ? _dkimPrivateKeyPem : null),
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
                DataProtectionKeys = [PortableCookieKeyFixture.Create()],
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
