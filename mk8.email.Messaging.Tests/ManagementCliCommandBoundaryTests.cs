using System.Diagnostics;
using System.Text.Json;
using mk8.email.Configuration;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ManagementCliCommandBoundaryTests
{
    [TestMethod]
    public async Task ManagementCliRecognizesBoundedCommandsButRejectsListenerMode()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mk8email-missing-{Guid.NewGuid():N}");
        string[][] recognizedCommands =
        [
            ["--healthcheck"],
            ["--initialize-empty-database"],
            ["--ensure-runtime-schema"],
            ["--ensure-domain", "example.test", "Example"],
            ["--create-account", "person@example.test", "User", missing],
            ["--set-catchall", "example.test", "person@example.test"],
            ["--set-domain-active", "example.test", "true"],
            ["--create-app-password", "person@example.test", "device"],
            ["--list-app-passwords", "person@example.test"],
            ["--revoke-app-password", "person@example.test", Guid.Empty.ToString("D")],
            ["--purge-quarantined-smoke-message", missing, new string('a', 32), "probe@example.invalid"],
            ["--export-distributed-snapshot", missing, missing],
            ["--restore-distributed-snapshot", missing, missing],
            ["--verify-distributed-snapshot", missing],
            ["--publish-distributed-archive", missing, "backups", "archive-id", missing, missing],
            ["--fetch-distributed-archive", missing, "backups", "archive-id", missing, missing],
            ["--enroll-totp", "person@example.test", "password"],
            ["--confirm-totp", "person@example.test", "123456"],
            ["--totp-status", "person@example.test"],
            ["--regenerate-recovery-codes", "person@example.test"],
            ["--disable-totp", "person@example.test"],
            ["--validate-gateway-config", missing],
            ["--validate-worker-config", missing],
            ["--healthcheck-gateway", missing],
            ["--probe-gateway-backends", missing],
            ["--probe-worker-backends", missing],
            ["--probe-worker-dispatch", missing],
            ["--probe-gateway-dispatch", missing],
            ["--audit-blob-references", missing],
            ["--audit-messaging-keys", missing],
            ["--worker-wake-schema-state", missing, missing],
            ["--prepare-worker-wake", missing, missing],
            ["--restore-worker-wake", missing, missing, "legacy"],
        ];

        foreach (var command in recognizedCommands)
        {
            var (exitCode, output) = await RunCliAsync(missing, command).ConfigureAwait(false);
            Assert.AreEqual(1, exitCode,
                $"{command[0]} must be recognized and fail on the missing prerequisite: {output}");
            Assert.IsFalse(output.Contains("Use one valid management command.", StringComparison.Ordinal));
        }

        string[][] rejectedCommands =
        [
            ["--serve"],
            ["--ensure-domain", "example.test"],
            ["--unknown"],
            ["--prepare-worker-wake", missing],
        ];
        foreach (var command in rejectedCommands)
        {
            var (exitCode, output) = await RunCliAsync(missing, command).ConfigureAwait(false);
            Assert.AreEqual(2, exitCode, $"{command[0]}: {output}");
            StringAssert.Contains(output, "Use one valid management command.", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SmokeCleanupWithoutSenderRefusesBeforeConfiguration()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mk8email-missing-{Guid.NewGuid():N}");
        var (exitCode, output) = await RunCliAsync(missing,
            ["--purge-quarantined-smoke-message", missing, new string('a', 32)]).ConfigureAwait(false);
        Assert.AreEqual(2, exitCode, output);
        Assert.Contains("Use one valid management command.", output, StringComparison.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The WakeOperatorRejectsUnsafeFilesAndEndpointMismatchWithoutEchoingSecrets scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task WakeOperatorRejectsUnsafeFilesAndEndpointMismatchWithoutEchoingSecrets()
    {
        var directory = Directory.CreateTempSubdirectory("mk8-wake-cli-");
        try
        {
            var config = new EnvironmentConfig
            {
                Database = new DatabaseConfig { Password = "local-test-only" },
                Smtp = new SmtpConfig { Hostname = "mail.example.test" },
                Messaging = new MessagingConfig
                {
                    Enabled = true,
                    EncryptionKey = Convert.ToBase64String(new byte[32]),
                },
                ObjectStorage = new ObjectStorageConfig { ConnectionString = "UseDevelopmentStorage=true" },
            };
            var errors = config.Validate(true, EnvironmentValidationRole.ApplicationWorker);
            Assert.HasCount(0, errors, string.Join("; ", errors));
            var configPath = Path.Combine(directory.FullName, "worker.json");
            var wakePath = Path.Combine(directory.FullName, "wake.connection");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);
            const string secret = "secret-must-not-appear-in-command-output";
            await File.WriteAllTextAsync(wakePath, secret + "=not-a-connection-string").ConfigureAwait(false);
            var (malformedExitCode, malformedOutput) = await RunCliAsync(configPath,
                ["--prepare-worker-wake", configPath, wakePath], development: true).ConfigureAwait(false);
            Assert.AreEqual(1, malformedExitCode, malformedOutput);
            StringAssert.Contains(malformedOutput, "not a valid PostgreSQL connection string", StringComparison.Ordinal);
            Assert.IsFalse(malformedOutput.Contains(secret, StringComparison.Ordinal));

            foreach (var changed in new[] { "host", "port", "database", "role" })
            {
                var connection = new NpgsqlConnectionStringBuilder(config.BuildConnectionString())
                { Username = "mk8wake", Password = secret };
                switch (changed)
                {
                    case "host": connection.Host = "other.example.test"; break;
                    case "port": connection.Port++; break;
                    case "database": connection.Database = "other_database"; break;
                    case "role": connection.Username = config.Database.Username; break;
                }
                await File.WriteAllTextAsync(wakePath, connection.ConnectionString).ConfigureAwait(false);
                var (mismatchExitCode, mismatchOutput) = await RunCliAsync(configPath,
                    ["--prepare-worker-wake", configPath, wakePath], development: true).ConfigureAwait(false);
                Assert.AreEqual(1, mismatchExitCode, mismatchOutput);
                StringAssert.Contains(mismatchOutput, "separate roles on the same explicit database endpoint", StringComparison.Ordinal);
                Assert.IsFalse(mismatchOutput.Contains(secret, StringComparison.Ordinal));
            }
            if (OperatingSystem.IsLinux())
            {
                var link = Path.Combine(directory.FullName, "wake-link.connection");
                File.CreateSymbolicLink(link, wakePath);
                var (symlinkExitCode, symlinkOutput) = await RunCliAsync(configPath,
                    ["--prepare-worker-wake", configPath, link], development: true).ConfigureAwait(false);
                Assert.AreEqual(1, symlinkExitCode, symlinkOutput);
                StringAssert.Contains(symlinkOutput, "missing, oversized or unsafe", StringComparison.Ordinal);
                if (!string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
                {
                    var (serviceExitCode, serviceOutput) = await RunCliAsync(configPath,
                        ["--prepare-worker-wake", configPath, wakePath]).ConfigureAwait(false);
                    Assert.AreEqual(1, serviceExitCode, serviceOutput);
                    StringAssert.Contains(serviceOutput, "require a root operator", StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static async Task<(int ExitCode, string Output)> RunCliAsync(
        string missingConfig,
        IReadOnlyList<string> arguments,
        bool development = false)
    {
        var host = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? "dotnet";
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is missing.");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var assembly = Path.Combine(
            repository, "mk8.email.CLI", "bin", configuration,
            "net10.0", "mk8.email.Application.CLI.dll");
        Assert.IsTrue(File.Exists(assembly), "The management CLI executable is missing.");
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["MK8EMAIL_CONFIG_FILE"] = missingConfig;
        start.Environment["DOTNET_ENVIRONMENT"] = development ? "Development" : "Production";
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The management CLI did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail($"The management CLI timed out: {arguments[0]}");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
