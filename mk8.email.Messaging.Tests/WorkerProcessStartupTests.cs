using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class WorkerProcessStartupTests
{
    private const long QueueMigrationLockKey = 3_415_682_194_307_812_221;

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The PreparedOneShotWorkerDrainsWithoutRepeatingBlobMigration scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task PreparedOneShotWorkerDrainsWithoutRepeatingBlobMigration()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var blobConnection = RequireAzureBlobConnection();
        var containerName = "mk8-worker-test-" + Guid.NewGuid().ToString("N");
        var container = new BlobServiceClient(blobConnection)
            .GetBlobContainerClient(containerName);
        var directory = Directory.CreateTempSubdirectory("mk8-worker-process-");
        try
        {
            {
                var context = new EmailDbContext(
                             new DbContextOptionsBuilder<EmailDbContext>()
                                 .UseNpgsql(database.ConnectionString).Options);
                await using var contextLifetime = context.ConfigureAwait(false);
                await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            }

            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
            var config = new EnvironmentConfig
            {
                Database = new DatabaseConfig
                {
                    Host = connection.Host!,
                    Port = connection.Port,
                    Name = connection.Database!,
                    Username = connection.Username!,
                    Password = string.IsNullOrEmpty(connection.Password)
                        ? "local-test-only-password"
                        : connection.Password,
                },
                Smtp = new SmtpConfig { Hostname = "worker.example.test" },
                Jmap = new JmapConfig { EnableJmap = false, IsDefault = false },
                Dav = new DavConfig { EnableDav = false },
                Messaging = new MessagingConfig
                {
                    Enabled = true,
                    EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                },
                ObjectStorage = new ObjectStorageConfig
                {
                    ConnectionString = blobConnection,
                    ContainerName = containerName,
                    CreateContainerIfMissing = true,
                },
            };
            Assert.HasCount(0, config.Validate(
                isDevelopment: false, EnvironmentValidationRole.ApplicationWorker));
            var configPath = Path.Combine(directory.FullName, "worker.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);

            var prepared = await RunWorkerAsync("--prepare", configPath, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            Assert.AreEqual(0, prepared.ExitCode, prepared.Output);
            StringAssert.Contains(prepared.Output, "schemas and Azure Blob references are prepared", StringComparison.Ordinal);

            var lockConnection = new NpgsqlConnection(database.ConnectionString);
            await using var lockConnectionLifetime = lockConnection.ConfigureAwait(false);
            await lockConnection.OpenAsync().ConfigureAwait(false);
            {
                var acquire = lockConnection.CreateCommand();
                await using var acquireLifetime = acquire.ConfigureAwait(false);
                acquire.CommandText = "SELECT pg_advisory_lock(@key)";
                acquire.Parameters.AddWithValue("key", QueueMigrationLockKey);
                await acquire.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            try
            {
                var drained = await RunWorkerAsync("--drain", configPath, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Assert.AreEqual(0, drained.ExitCode, drained.Output);
                StringAssert.Contains(drained.Output, "drained 0 requests and 0 queued messages", StringComparison.Ordinal);
            }
            finally
            {
                var release = lockConnection.CreateCommand();
                await using var releaseLifetime = release.ConfigureAwait(false);
                release.CommandText = "SELECT pg_advisory_unlock(@key)";
                release.Parameters.AddWithValue("key", QueueMigrationLockKey);
                await release.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
            directory.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunWorkerAsync(
        string mode,
        string configPath,
        TimeSpan timeout)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? "dotnet";
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is missing.");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var assembly = Path.Combine(
            repository, "mk8.email.Application.Worker", "bin", configuration,
            "net10.0", "mk8.email.Application.Worker.dll");
        Assert.IsTrue(File.Exists(assembly), "The built Worker executable is missing.");
        var start = new ProcessStartInfo(dotnetHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(mode);
        start.Environment[EnvironmentLoader.ConfigPathVariable] = configPath;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The test Worker process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail($"The Worker {mode} process exceeded {timeout}.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration tests require a database.");
        }
        return database;
    }

    private static string RequireAzureBlobConnection()
    {
        var connection = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION.");
            throw new InvalidOperationException("Azure Blob integration tests require a connection.");
        }
        return connection;
    }
}
