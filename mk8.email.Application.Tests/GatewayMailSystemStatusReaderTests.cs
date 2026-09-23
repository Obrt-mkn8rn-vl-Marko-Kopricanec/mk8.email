using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Configuration;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayMailSystemStatusReaderTests
{
    private string _testDirectory = null!;
    private string _statusPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _statusPath = Path.Combine(_testDirectory, "status.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    public async Task ValidSnapshotReturnsOperationalMetrics()
    {
        await File.WriteAllTextAsync(
            _statusPath,
            """
            {
              "state": "healthy",
              "checkedAt": "2026-09-04T01:30:00Z",
              "queueCount": 3,
              "oldestQueuedMessageSeconds": 45,
              "mailStorageUsedPercent": 12,
              "backupExportAgeSeconds": 3600,
              "clamSignatureAgeSeconds": 1800,
              "mailCertificateRemainingSeconds": 864000,
              "adminCertificateRemainingSeconds": 1728000,
              "errorCount": 0
            }
            """);
        var service = CreateService();

        var result = await service.GetStatusAsync();

        Assert.AreEqual("healthy", result.State);
        Assert.AreEqual(3, result.QueueCount);
        Assert.AreEqual(12, result.MailStorageUsedPercent);
        Assert.AreEqual(0, result.ErrorCount);
    }

    [TestMethod]
    public async Task InvalidSnapshotReturnsUnavailableState()
    {
        await File.WriteAllTextAsync(_statusPath, "{\"state\":\"healthy\",\"queueCount\":-1}");
        var service = CreateService();

        var result = await service.GetStatusAsync();

        Assert.AreEqual("unavailable", result.State);
        Assert.IsNull(result.CheckedAt);
    }

    private GatewayMailSystemStatusReader CreateService() => new(
        new EnvironmentConfig
        {
            Admin = new AdminConfig { HealthStatusPath = _statusPath },
        },
        NullLogger<GatewayMailSystemStatusReader>.Instance);
}
