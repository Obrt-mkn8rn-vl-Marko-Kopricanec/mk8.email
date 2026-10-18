using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;
using mk8.email.CLI;
using mk8.email.Configuration;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers this finite internal bootstrap fixture by reflection.")]
internal sealed class PortableManagementBootstrapTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow("Archive")]
    [DataRow("Wake")]
    public async Task StandaloneRoleOutlinesBindWithoutImportingServiceCredentials(string role)
    {
        Assert.IsNotNull(TestContext);
        var token = TestContext.CancellationToken;
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "RoleTemplates", role + ".env.template"), token).ConfigureAwait(false);
        var settings = Supprocom.Secrets.SupprocomSecretDocument.Parse(text).Settings;
        Assert.IsNotEmpty(settings);
        Assert.IsTrue(settings.All(setting => setting.Value.Length == 0));
        using var source = new ConfigurationManager();
        source.AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(
            setting.Key, setting.Key switch
            {
                "DatabasePort" => "5432",
                "DatabaseSslMode" => "VerifyFull",
                "IncludeJmap" => "true",
                _ => "fixture",
            })));
        if (string.Equals(role, "Archive", StringComparison.Ordinal))
        {
            var archive = source.Get<ManagementConfig>(binder => binder.ErrorOnUnknownConfiguration = true);
            Assert.IsNotNull(archive);
            Assert.IsNull(archive.AccountPassword);
            Assert.IsNull(archive.WakeDatabaseHost);
        }
        else
        {
            var wake = source.Get<WakeConfig>(binder => binder.ErrorOnUnknownConfiguration = true);
            Assert.IsNotNull(wake);
            Assert.AreEqual(5432, wake.DatabasePort);
        }
    }

    [TestMethod]
    public void ManagementHostIgnoresAmbientEnvironmentAndHasNoDefaultProviders()
    {
        const string canary = "ManagementBootstrapCanary";
        var priorEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var priorCanary = Environment.GetEnvironmentVariable(canary);
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable(canary, "not-an-admitted-setting");
            var builder = ManagementBootstrap.CreateBuilder();
            using var host = builder.Build();
            Assert.AreEqual(Environments.Production, builder.Environment.EnvironmentName, StringComparer.Ordinal);
            Assert.AreEqual(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(builder.Environment.ContentRootPath), StringComparer.Ordinal);
            Assert.IsNull(builder.Configuration[canary]);
            foreach (var provider in ((IConfigurationRoot)builder.Configuration).Providers)
                Assert.IsInstanceOfType<MemoryConfigurationProvider>(provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", priorEnvironment);
            Environment.SetEnvironmentVariable(canary, priorCanary);
        }
    }

    [TestMethod]
    public void CommandShapesRejectLegacyLocatorsAndPreserveBusinessOperands()
    {
        string[][] accepted =
        [
            ["--validate-gateway-config"], ["--validate-worker-config"], ["--prepare-worker-wake"],
            ["--restore-worker-wake", "legacy"], ["--restore-worker-wake", "receipts"],
            ["--create-account", "person@example.test", "User"], ["--enroll-totp", "person@example.test"],
            ["--confirm-totp", "person@example.test"], ["--publish-distributed-archive", "fixture-id", "/owned-artifact"],
            ["--fetch-distributed-archive", "fixture-id", "/owned-destination"],
            ["--purge-quarantined-smoke-message", new string('a', 32), "probe@example.invalid"],
        ];
        foreach (var arguments in accepted) Assert.IsTrue(ManagementBootstrap.IsSupported(arguments), arguments[0]);
        string[][] refused =
        [
            ["--serve"], ["--dev"], ["--validate-gateway-config", "retired.json"],
            ["--prepare-worker-wake", "retired.json", "retired.connection"],
            ["--create-account", "person@example.test", "User", "retired.password"],
            ["--enroll-totp", "person@example.test", "retired-cli-password"],
            ["--confirm-totp", "person@example.test", "123456"],
            ["--publish-distributed-archive", "retired.connection", "container", "id", "/artifact", "retired.pem"],
            ["--purge-quarantined-smoke-message", new string('a', 32)],
            ["--restore-worker-wake", "unknown"],
        ];
        foreach (var arguments in refused) Assert.IsFalse(ManagementBootstrap.IsSupported(arguments), arguments[0]);
    }

    [TestMethod]
    [DataRow("https://account.blob.example.test", true)]
    [DataRow("http://127.0.0.1:10000/account", true)]
    [DataRow("http://account.blob.example.test", false)]
    public void ArchiveClientUsesOnlyExplicitTlsOrLiteralLoopback(string endpoint, bool admitted)
    {
        var configuration = new ManagementConfig
        {
            ArchiveConnectionString = "AccountName=account;AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;BlobEndpoint=" + endpoint,
            ArchiveContainerName = "owned-archives",
        };
        // Construction only; no request, account or network is adopted.
        if (admitted)
            Assert.AreEqual(new Uri(endpoint), ManagementBootstrap.CreateArchiveClient(configuration).Uri);
        else
            Assert.ThrowsExactly<InvalidOperationException>(() => ManagementBootstrap.CreateArchiveClient(configuration));
    }

    [TestMethod]
    public void WakeTransitionRetainsDistinctRoleAndExactEndpointAdmission()
    {
        foreach (var changed in new[] { "none", "host", "port", "database", "role" })
        {
            var configuration = new EnvironmentConfig
            {
                Database = new DatabaseConfig { Host = "database.example.test", Port = 5432, Name = "fixture", Username = "worker" },
                Management = new ManagementConfig
                {
                    WakeDatabaseHost = string.Equals(changed, "host", StringComparison.Ordinal) ? "other.example.test" : "database.example.test",
                    WakeDatabasePort = string.Equals(changed, "port", StringComparison.Ordinal) ? 5433 : 5432,
                    WakeDatabaseName = string.Equals(changed, "database", StringComparison.Ordinal) ? "other" : "fixture",
                    WakeDatabaseUsername = string.Equals(changed, "role", StringComparison.Ordinal) ? "worker" : "wake",
                },
            };
            if (string.Equals(changed, "none", StringComparison.Ordinal)) Assert.AreEqual("wake", WorkerWakeOperatorCommand.GetWakeRole(configuration), StringComparer.Ordinal);
            else Assert.ThrowsExactly<InvalidOperationException>(() => WorkerWakeOperatorCommand.GetWakeRole(configuration));
        }
        if (!OperatingSystem.IsLinux() || !string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
            Assert.ThrowsExactly<InvalidOperationException>(WorkerWakeOperatorCommand.RequireOperator);
    }
}
