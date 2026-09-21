using System.Text.Json;
using mk8.email.Infrastructure.Environment;

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
        int oauthRefreshTokenDays = 90)
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
                AccessTokenMinutes = oauthAccessTokenMinutes,
                RefreshTokenDays = oauthRefreshTokenDays,
            },
            Tls = new TlsConfig
            {
                CertificatePath = _certificatePath,
            },
            Dkim = new DkimConfig
            {
                PrivateKeyPath = enableDkimSigning ? _certificatePath : null,
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
        };
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
