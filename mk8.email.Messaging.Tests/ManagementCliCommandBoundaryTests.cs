using System.Diagnostics;

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
            ["--create-account", "person@example.test", "User"],
            ["--set-catchall", "example.test", "person@example.test"],
            ["--set-domain-active", "example.test", "true"],
            ["--create-app-password", "person@example.test", "device"],
            ["--list-app-passwords", "person@example.test"],
            ["--revoke-app-password", "person@example.test", Guid.Empty.ToString("D")],
            ["--purge-quarantined-smoke-message", new string('a', 32), "probe@example.invalid"],
            ["--export-distributed-snapshot", missing],
            ["--restore-distributed-snapshot", missing],
            ["--verify-distributed-snapshot", missing],
            ["--publish-distributed-archive", "archive-id", missing],
            ["--fetch-distributed-archive", "archive-id", missing],
            ["--enroll-totp", "person@example.test"],
            ["--confirm-totp", "person@example.test"],
            ["--totp-status", "person@example.test"],
            ["--regenerate-recovery-codes", "person@example.test"],
            ["--disable-totp", "person@example.test"],
            ["--validate-gateway-config"],
            ["--validate-worker-config"],
            ["--healthcheck-gateway"],
            ["--probe-gateway-backends"],
            ["--probe-worker-backends"],
            ["--probe-worker-dispatch"],
            ["--probe-gateway-dispatch"],
            ["--audit-blob-references"],
            ["--audit-messaging-keys"],
            ["--worker-wake-schema-state"],
            ["--prepare-worker-wake"],
            ["--restore-worker-wake", "legacy"],
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
            ["--purge-quarantined-smoke-message", new string('a', 32)]).ConfigureAwait(false);
        Assert.AreEqual(2, exitCode, output);
        Assert.Contains("Use one valid management command.", output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task LegacyWakeFileArgumentsRefuseBeforeConfigurationOrSecretReads()
    {
        var directory = Directory.CreateTempSubdirectory("mk8-wake-cli-refusal-");
        try
        {
            const string secret = "synthetic-old-file-value-never-admitted";
            var config = Path.Combine(directory.FullName, "retired.json");
            var connection = Path.Combine(directory.FullName, "retired.connection");
            await File.WriteAllTextAsync(config, secret).ConfigureAwait(false);
            await File.WriteAllTextAsync(connection, secret).ConfigureAwait(false);
            string[][] arguments =
            [
                ["--worker-wake-schema-state", config, connection],
                ["--prepare-worker-wake", config, connection],
                ["--restore-worker-wake", config, connection, "legacy"],
            ];
            foreach (var command in arguments)
            {
                var (exitCode, output) = await RunCliAsync(config, command, development: true).ConfigureAwait(false);
                Assert.AreEqual(2, exitCode, output);
                Assert.Contains("Use one valid management command.", output, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
            }
            Assert.AreEqual(secret, await File.ReadAllTextAsync(config).ConfigureAwait(false), StringComparer.Ordinal);
            Assert.AreEqual(secret, await File.ReadAllTextAsync(connection).ConfigureAwait(false), StringComparer.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
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
        var assembly = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_CLI_DLL") ?? Path.Combine(
            repository, "mk8.email.CLI", "bin", configuration,
            "net10.0", "mk8.email.Application.CLI.dll");
        Assert.IsTrue(File.Exists(assembly), "The management CLI executable is missing.");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(assembly)!, "Environment", ".env")))
            Assert.Inconclusive("Command-refusal controls require compiled inputs without an active role document.");
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
