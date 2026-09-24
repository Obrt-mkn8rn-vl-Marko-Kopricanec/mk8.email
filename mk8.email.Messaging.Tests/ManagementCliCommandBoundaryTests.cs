using System.Diagnostics;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class ManagementCliCommandBoundaryTests
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
            ["--purge-quarantined-smoke-message", missing, new string('a', 32)],
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
            ["--audit-blob-references", missing],
        ];

        foreach (var command in recognizedCommands)
        {
            var result = await RunCliAsync(missing, command);
            Assert.AreEqual(1, result.ExitCode,
                $"{command[0]} must be recognized and fail on the missing prerequisite: {result.Output}");
            Assert.IsFalse(result.Output.Contains("Use one valid management command.", StringComparison.Ordinal));
        }

        string[][] rejectedCommands =
        [
            ["--serve"],
            ["--ensure-domain", "example.test"],
            ["--unknown"],
        ];
        foreach (var command in rejectedCommands)
        {
            var result = await RunCliAsync(missing, command);
            Assert.AreEqual(2, result.ExitCode, $"{command[0]}: {result.Output}");
            StringAssert.Contains(result.Output, "Use one valid management command.");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(
        string missingConfig,
        IReadOnlyList<string> arguments)
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
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"The management CLI timed out: {arguments[0]}");
        }
        return (process.ExitCode, await output + await error);
    }
}
