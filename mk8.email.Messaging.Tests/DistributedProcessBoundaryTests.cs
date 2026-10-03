using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DistributedProcessBoundaryTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The GatewayJournalsWhileWorkerIsStoppedAndWakeTriggersLaterDispatchAcrossProcesses scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task GatewayJournalsWhileWorkerIsStoppedAndWakeTriggersLaterDispatchAcrossProcesses()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var blobConnection = RequireAzureBlobConnection();
        var container = new BlobServiceClient(blobConnection)
            .GetBlobContainerClient("mk8-process-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateTempSubdirectory("mk8-process-boundary-");
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
                Smtp = new SmtpConfig
                {
                    Hostname = "email.example.test",
                    EnableSmtp = false,
                },
                Imap = new ImapConfig { EnableImap = false, EnableImplicitTls = false },
                Pop3 = new Pop3Config { EnablePop3 = false, EnableImplicitTls = false },
                Sieve = new SieveConfig { EnableManageSieve = false },
                Jmap = new JmapConfig
                {
                    EnableJmap = true,
                    IsDefault = true,
                    PublicBaseUrl = "https://email.example.test",
                },
                Dav = new DavConfig { EnableDav = false },
                Admin = new AdminConfig
                {
                    AllowedNetworks = ["127.0.0.0/8"],
                    DataProtectionKeyPath = Path.Combine(directory.FullName, "keys"),
                    AuditLogPath = Path.Combine(directory.FullName, "audit.jsonl"),
                    HealthStatusPath = Path.Combine(directory.FullName, "status.json"),
                },
                Messaging = new MessagingConfig
                {
                    Enabled = true,
                    EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                },
                ObjectStorage = new ObjectStorageConfig
                {
                    ConnectionString = blobConnection,
                    ContainerName = container.Name,
                    CreateContainerIfMissing = true,
                },
            };
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.Gateway));
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.ApplicationWorker));
            Directory.CreateDirectory(config.Admin.DataProtectionKeyPath);
            var configPath = Path.Combine(directory.FullName, "distributed.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);

            var prepared = await RunWorkerAsync("--prepare", configPath).ConfigureAwait(false);
            Assert.AreEqual(0, prepared.ExitCode, prepared.Output);

            var wakeRole = $"mk8_process_wake_{Guid.NewGuid():N}";
            var wakePassword = Guid.NewGuid().ToString("N");
            var gatewayRole = $"mk8_process_gateway_{Guid.NewGuid():N}";
            var gatewayPassword = Guid.NewGuid().ToString("N");
            var roleAdmin = new NpgsqlConnection(database.ConnectionString);
            await using var roleAdminLifetime = roleAdmin.ConfigureAwait(false);
            await roleAdmin.OpenAsync().ConfigureAwait(false);
            await CreateRestrictedWakeRoleAsync(
                roleAdmin, database.DatabaseName, wakeRole, wakePassword).ConfigureAwait(false);
            var gatewayRoleCreated = false;
            try
            {
                await CreateRestrictedGatewayRoleAsync(
                    roleAdmin, database.DatabaseName, gatewayRole, gatewayPassword).ConfigureAwait(false);
                gatewayRoleCreated = true;
                var gatewayConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
                {
                    Username = gatewayRole,
                    Password = gatewayPassword,
                };
                {
                    var gatewayDataSource = NpgsqlDataSource.Create(
                                 gatewayConnection.ConnectionString);
                    await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
                    await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource).ConfigureAwait(false);
                    var applicationTable = gatewayDataSource.CreateCommand(
                        "SELECT id FROM users");
                    await using var applicationTableLifetime = applicationTable.ConfigureAwait(false);
                    var denial = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => applicationTable.ExecuteScalarAsync()).ConfigureAwait(false);
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, denial.SqlState, StringComparer.Ordinal);
                }
                var gatewayConfig = JsonSerializer.SerializeToNode(config)!.AsObject();
                gatewayConfig["Database"]!["Username"] = gatewayRole;
                gatewayConfig["Database"]!["Password"] = gatewayPassword;
                gatewayConfig["Admin"]!["DataProtectionKeyPath"] =
                    Path.Combine(directory.FullName, "gateway-keys");
                gatewayConfig["Admin"]!["AuditLogPath"] =
                    Path.Combine(directory.FullName, "gateway-audit.jsonl");
                gatewayConfig["Admin"]!["HealthStatusPath"] =
                    Path.Combine(directory.FullName, "gateway-status.json");
                Directory.CreateDirectory(
                    gatewayConfig["Admin"]!["DataProtectionKeyPath"]!.GetValue<string>());
                var gatewayConfigPath = Path.Combine(directory.FullName, "gateway.json");
                await File.WriteAllTextAsync(gatewayConfigPath, gatewayConfig.ToJsonString()).ConfigureAwait(false);

                var wakeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
                {
                    Username = wakeRole,
                    Password = wakePassword,
                    Pooling = false,
                };
                var wakeConnectionPath = Path.Combine(directory.FullName, "wake-connection.txt");
                await File.WriteAllTextAsync(wakeConnectionPath, wakeConnection.ConnectionString).ConfigureAwait(false);
                var triggerDirectory = Path.Combine(directory.FullName, "wake-signals");
                Directory.CreateDirectory(triggerDirectory);

                var port = ReserveLoopbackPort();
                using var gateway = StartProcess(
                    "mk8.email.Gateway", "mk8.email.Gateway.dll", null, gatewayConfigPath,
                    $"http://127.0.0.1:{port}");
                var gatewayOutput = gateway.StandardOutput.ReadToEndAsync();
                var gatewayErrors = gateway.StandardError.ReadToEndAsync();
                try
                {
                    using var client = new HttpClient
                    {
                        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                        Timeout = TimeSpan.FromSeconds(35),
                    };
                    await WaitForGatewayAsync(client, gateway, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                    Assert.AreEqual(HttpStatusCode.OK,
                        (await client.GetAsync(new Uri("/health/live", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                    Assert.AreEqual(HttpStatusCode.OK,
                        (await client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                    Assert.AreEqual(HttpStatusCode.Unauthorized,
                        (await client.GetAsync(new Uri("/.well-known/jmap", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);

                    var inspection = new NpgsqlConnection(database.ConnectionString);
                    await using var inspectionLifetime = inspection.ConfigureAwait(false);
                    await inspection.OpenAsync().ConfigureAwait(false);
                    Assert.AreEqual(2, await CountAsync(
                        inspection,
                        "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'jmap'").ConfigureAwait(false));

                    // The reverse presentation lane remains usable without an Application
                    // process and without granting Gateway access to Application tables.
                    {
                        var applicationDataSource = NpgsqlDataSource.Create(
                                     database.ConnectionString);
                        await using var applicationDataSourceLifetime = applicationDataSource.ConfigureAwait(false);
                        using (var protector = new AesGcmPayloadProtector(new MessagingEncryptionKey(
                                   config.Messaging.EncryptionKeyId,
                                   Convert.FromBase64String(config.Messaging.EncryptionKey))))
                        {
                            var presentation = new JmapPushPresentationClient(
                                new PostgresPresentationBus(applicationDataSource, protector),
                                NullLogger<JmapPushPresentationClient>.Instance);
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            Assert.IsFalse(await presentation.IsSafeUrlAsync(
                                "https://127.0.0.1/push", timeout.Token).ConfigureAwait(false));
                        }
                    }
                    Assert.AreEqual(1, await CountAsync(
                        inspection,
                        "SELECT count(*) FROM presentation_requests "
                        + "WHERE protocol = 'webpush' AND state = 'completed'").ConfigureAwait(false));
                    Assert.AreEqual(2, await CountAsync(
                        inspection,
                        "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'webpush'").ConfigureAwait(false));

                    var pendingProbe = client.GetAsync(new Uri("/health/application", UriKind.RelativeOrAbsolute));
                    await WaitForQueuedProbeAsync(inspection, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    Assert.IsFalse(gateway.HasExited);
                    Assert.IsFalse(pendingProbe.IsCompleted);
                    Assert.AreEqual(HttpStatusCode.OK,
                        (await client.GetAsync(new Uri("/health/live", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);

                    using var wake = StartWakeProcess(wakeConnectionPath, triggerDirectory);
                    var wakeOutput = wake.StandardOutput.ReadToEndAsync();
                    var wakeErrors = wake.StandardError.ReadToEndAsync();
                    try
                    {
                        var trigger = Path.Combine(triggerDirectory, "trigger");
                        await WaitForWakeTriggerAsync(trigger, wake, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                        Assert.IsFalse(pendingProbe.IsCompleted);
                        Assert.AreEqual(1, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM application_requests "
                            + "WHERE protocol = 'health' AND state = 'pending'").ConfigureAwait(false));
                        File.Delete(trigger); // The path unit consumes the signal before --drain.

                        var drained = await RunWorkerAsync("--drain", configPath).ConfigureAwait(false);
                        Assert.AreEqual(0, drained.ExitCode, drained.Output);
                        Assert.AreEqual(HttpStatusCode.OK, (await pendingProbe.ConfigureAwait(false)).StatusCode);
                        Assert.AreEqual(1, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM application_requests "
                            + "WHERE protocol = 'health' AND state = 'completed'").ConfigureAwait(false));
                        Assert.AreEqual(2, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'health'").ConfigureAwait(false));
                        Assert.IsFalse(gateway.HasExited);
                        Assert.IsFalse(wake.HasExited);
                    }
                    finally
                    {
                        if (!wake.HasExited)
                            wake.Kill(entireProcessTree: true);
                        await wake.WaitForExitAsync().ConfigureAwait(false);
                        await Task.WhenAll(wakeOutput, wakeErrors).ConfigureAwait(false);
                    }

                    var shallowProbe = await RunWakeDatabaseProbeAsync(wakeConnectionPath).ConfigureAwait(false);
                    Assert.AreEqual(0, shallowProbe.ExitCode, shallowProbe.Output);
                    StringAssert.Contains(shallowProbe.Output, "due=false", StringComparison.Ordinal);

                    using var containerWake = StartWorkerLaunchingWakeProcess(
                        wakeConnectionPath, configPath);
                    var containerWakeOutput = containerWake.StandardOutput.ReadToEndAsync();
                    var containerWakeErrors = containerWake.StandardError.ReadToEndAsync();
                    try
                    {
                        Assert.AreEqual(HttpStatusCode.OK,
                            (await client.GetAsync(new Uri("/health/application", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                        Assert.AreEqual(2, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM application_requests "
                            + "WHERE protocol = 'health' AND state = 'completed'").ConfigureAwait(false));
                        Assert.AreEqual(4, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'health'").ConfigureAwait(false));
                        Assert.IsFalse(containerWake.HasExited);
                        Assert.IsFalse(gateway.HasExited);
                    }
                    finally
                    {
                        if (!containerWake.HasExited)
                            containerWake.Kill(entireProcessTree: true);
                        await containerWake.WaitForExitAsync().ConfigureAwait(false);
                        await Task.WhenAll(containerWakeOutput, containerWakeErrors).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!gateway.HasExited)
                        gateway.Kill(entireProcessTree: true);
                    await gateway.WaitForExitAsync().ConfigureAwait(false);
                    await Task.WhenAll(gatewayOutput, gatewayErrors).ConfigureAwait(false);
                }
            }
            finally
            {
                var cleanup = roleAdmin.CreateCommand();
                await using var cleanupLifetime = cleanup.ConfigureAwait(false);

                // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                cleanup.CommandText = gatewayRoleCreated
                    ? $"""
                        DROP OWNED BY "{gatewayRole}";
                        DROP ROLE "{gatewayRole}";
                        DROP OWNED BY "{wakeRole}";
                        DROP ROLE "{wakeRole}";
                        """
                    : $"DROP OWNED BY \"{wakeRole}\"; DROP ROLE \"{wakeRole}\"";

#pragma warning restore CA2100

                await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The PendingRestoreKeepsGatewayLiveButBlocksTrafficUntilItIsComplete scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task PendingRestoreKeepsGatewayLiveButBlocksTrafficUntilItIsComplete()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var directory = Directory.CreateTempSubdirectory("mk8-pending-restore-process-");
        try
        {
            {
                var connection = new NpgsqlConnection(database.ConnectionString);
                await using var connectionLifetime = connection.ConfigureAwait(false);
                await connection.OpenAsync().ConfigureAwait(false);
                var create = connection.CreateCommand();
                await using var createLifetime = create.ConfigureAwait(false);
                create.CommandText = """
                    CREATE TABLE public.mk8_restore_state (
                        id smallint PRIMARY KEY,
                        state text NOT NULL);
                    INSERT INTO public.mk8_restore_state VALUES (1, 'pending');
                    """;
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var config = CreateGatewayTestConfig(
                new NpgsqlConnectionStringBuilder(database.ConnectionString), directory);
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.Gateway));
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.ApplicationWorker));
            Directory.CreateDirectory(config.Admin.DataProtectionKeyPath);
            var configPath = Path.Combine(directory.FullName, "distributed.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);

            foreach (var mode in new[] { "--prepare", "--drain" })
            {
                var worker = await RunWorkerAsync(mode, configPath).ConfigureAwait(false);
                Assert.AreEqual(1, worker.ExitCode, worker.Output);
                StringAssert.Contains(worker.Output, "restore is incomplete", StringComparison.Ordinal);
            }

            var port = ReserveLoopbackPort();
            using var gateway = StartProcess(
                "mk8.email.Gateway", "mk8.email.Gateway.dll", null, configPath,
                $"http://127.0.0.1:{port}");
            var gatewayOutput = gateway.StandardOutput.ReadToEndAsync();
            var gatewayErrors = gateway.StandardError.ReadToEndAsync();
            try
            {
                using var client = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                    Timeout = TimeSpan.FromSeconds(15),
                };
                await WaitForGatewayAsync(client, gateway, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/.well-known/jmap", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/health/application", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);

                var inspection = new NpgsqlConnection(database.ConnectionString);
                await using var inspectionLifetime = inspection.ConfigureAwait(false);
                await inspection.OpenAsync().ConfigureAwait(false);
                Assert.AreEqual(0L, await CountAsync(
                    inspection,
                    "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users')").ConfigureAwait(false));

                // A restore can recreate messaging tables while its marker is still pending.
                {
                    var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
                    await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
                    await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
                }
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/.well-known/jmap", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/health/application", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(0L, await CountAsync(
                    inspection, "SELECT count(*) FROM gateway_traffic_records").ConfigureAwait(false));
                Assert.AreEqual(0L, await CountAsync(
                    inspection, "SELECT count(*) FROM application_requests").ConfigureAwait(false));

                {
                    var complete = inspection.CreateCommand();
                    await using var completeLifetime = complete.ConfigureAwait(false);
                    complete.CommandText =
                        "UPDATE public.mk8_restore_state SET state = 'complete' WHERE id = 1";
                    Assert.AreEqual(1, await complete.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
                Assert.AreEqual(HttpStatusCode.OK,
                    (await client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized,
                    (await client.GetAsync(new Uri("/.well-known/jmap", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(2L, await CountAsync(
                    inspection, "SELECT count(*) FROM gateway_traffic_records").ConfigureAwait(false));
                Assert.IsFalse(gateway.HasExited);
            }
            finally
            {
                if (!gateway.HasExited)
                    gateway.Kill(entireProcessTree: true);
                await gateway.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(gatewayOutput, gatewayErrors).ConfigureAwait(false);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PostgreSqlOutageLeavesGatewayLiveAndProtocolTrafficUnavailable()
    {
        var directory = Directory.CreateTempSubdirectory("mk8-gateway-db-outage-");
        try
        {
            var unavailableDatabase = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = ReserveLoopbackPort(),
                Database = "offline",
                Username = "offline",
                Password = "local-test-only-password",
            };
            var config = CreateGatewayTestConfig(unavailableDatabase, directory);
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.Gateway));
            Directory.CreateDirectory(config.Admin.DataProtectionKeyPath);
            var configPath = Path.Combine(directory.FullName, "distributed.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);

            var port = ReserveLoopbackPort();
            using var gateway = StartProcess(
                "mk8.email.Gateway", "mk8.email.Gateway.dll", null, configPath,
                $"http://127.0.0.1:{port}");
            var gatewayOutput = gateway.StandardOutput.ReadToEndAsync();
            var gatewayErrors = gateway.StandardError.ReadToEndAsync();
            try
            {
                using var client = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                    Timeout = TimeSpan.FromSeconds(15),
                };
                await WaitForGatewayAsync(client, gateway, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/.well-known/jmap", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync(new Uri("/health/application", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await client.GetAsync(new Uri("/health/live", UriKind.RelativeOrAbsolute)).ConfigureAwait(false)).StatusCode);
                Assert.IsFalse(gateway.HasExited);
            }
            finally
            {
                if (!gateway.HasExited)
                    gateway.Kill(entireProcessTree: true);
                await gateway.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(gatewayOutput, gatewayErrors).ConfigureAwait(false);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static EnvironmentConfig CreateGatewayTestConfig(
        NpgsqlConnectionStringBuilder database,
        DirectoryInfo directory) => new()
        {
            Database = new DatabaseConfig
            {
                Host = database.Host!,
                Port = database.Port,
                Name = database.Database!,
                Username = database.Username!,
                Password = string.IsNullOrEmpty(database.Password)
                ? "local-test-only-password"
                : database.Password,
            },
            Smtp = new SmtpConfig
            {
                Hostname = "email.example.test",
                EnableSmtp = false,
            },
            Imap = new ImapConfig { EnableImap = false, EnableImplicitTls = false },
            Pop3 = new Pop3Config { EnablePop3 = false, EnableImplicitTls = false },
            Sieve = new SieveConfig { EnableManageSieve = false },
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                IsDefault = true,
                PublicBaseUrl = "https://email.example.test",
            },
            Dav = new DavConfig { EnableDav = false },
            Admin = new AdminConfig
            {
                AllowedNetworks = ["127.0.0.0/8"],
                DataProtectionKeyPath = Path.Combine(directory.FullName, "keys"),
                AuditLogPath = Path.Combine(directory.FullName, "audit.jsonl"),
                HealthStatusPath = Path.Combine(directory.FullName, "status.json"),
            },
            Messaging = new MessagingConfig
            {
                Enabled = true,
                EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            },
            ObjectStorage = new ObjectStorageConfig
            {
                ConnectionString = "UseDevelopmentStorage=true",
                ContainerName = "mk8-gateway-test",
                CreateContainerIfMissing = true,
            },
        };

    private static async Task WaitForGatewayAsync(
        HttpClient client,
        Process gateway,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            Assert.IsFalse(gateway.HasExited, "Gateway stopped during startup.");
            try
            {
                using var response = await client.GetAsync(new Uri("/health/live", UriKind.RelativeOrAbsolute), cancellation.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
        }
        Assert.Fail("Gateway did not become live within its startup deadline.");
    }

    private static async Task WaitForQueuedProbeAsync(NpgsqlConnection connection, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            if (await CountAsync(
                    connection,
                    "SELECT count(*) FROM application_requests "
                    + "WHERE protocol = 'health' AND state = 'pending'").ConfigureAwait(false) > 0)
                return;
            await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
        }
        Assert.Fail("Gateway did not persist the Application probe while Worker was stopped.");
    }

    private static async Task WaitForWakeTriggerAsync(
        string path,
        Process wake,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            Assert.IsFalse(wake.HasExited, "The restricted Wake process stopped before signalling.");
            if (File.Exists(path))
                return;
            await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
        }
        Assert.Fail("Wake did not signal queued work within its deadline.");
    }

    private static async Task CreateRestrictedWakeRoleAsync(
        NpgsqlConnection admin,
        string databaseName,
        string role,
        string password)
    {
        var transaction = (await admin.BeginTransactionAsync().ConfigureAwait(false));
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var setup = admin.CreateCommand();
        await using var setupLifetime = setup.ConfigureAwait(false);
        setup.Transaction = transaction;

        // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
        setup.CommandText = $"""
            REVOKE ALL ON DATABASE "{databaseName}" FROM PUBLIC;
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
                NOBYPASSRLS NOINHERIT;
            GRANT CONNECT ON DATABASE "{databaseName}" TO "{role}";
            GRANT USAGE ON SCHEMA public TO "{role}";
            GRANT SELECT (state, lease_expires_at, deadline_at)
                ON application_requests TO "{role}";
            GRANT SELECT (effects_pending, effects_retry_at)
                ON application_operation_receipts TO "{role}";
            GRANT SELECT (state, next_attempt_at, lease_expires_at)
                ON mail_queue_messages TO "{role}";
            GRANT SELECT (expires_at, is_verified, next_push_at, user_id,
                last_pushed_change) ON jmap_push_subscriptions TO "{role}";
            GRANT SELECT (id, is_active) ON users TO "{role}";
            GRANT SELECT (id, owner_id, alias_for_inbox_id, name, address_id)
                ON inboxes TO "{role}";
            GRANT SELECT (id, company_id, is_active) ON addresses TO "{role}";
            GRANT SELECT (id, is_active) ON companies TO "{role}";
            GRANT SELECT (account_id, sequence) ON jmap_changes TO "{role}";
            """;

#pragma warning restore CA2100

        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task CreateRestrictedGatewayRoleAsync(
        NpgsqlConnection admin,
        string databaseName,
        string role,
        string password)
    {
        var transaction = (await admin.BeginTransactionAsync().ConfigureAwait(false));
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var setup = admin.CreateCommand();
        await using var setupLifetime = setup.ConfigureAwait(false);
        setup.Transaction = transaction;

        // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
        setup.CommandText = $"""
            CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
                NOBYPASSRLS NOINHERIT;
            GRANT CONNECT ON DATABASE "{databaseName}" TO "{role}";
            GRANT USAGE ON SCHEMA public TO "{role}";
            GRANT SELECT, INSERT ON gateway_traffic_records TO "{role}";
            GRANT SELECT, INSERT, UPDATE ON application_requests,
                presentation_requests TO "{role}";
            GRANT SELECT, INSERT, UPDATE, DELETE ON pop3_maildrop_leases TO "{role}";
            """;

#pragma warning restore CA2100

        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);

        // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
        command.CommandText = sql;

#pragma warning restore CA2100

        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The database count was missing."));
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<(int ExitCode, string Output)> RunWorkerAsync(
        string mode,
        string configPath)
    {
        using var process = StartProcess(
            "mk8.email.Application.Worker", "mk8.email.Application.Worker.dll", mode,
            configPath);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail($"Worker {mode} exceeded its process deadline.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
    }

    private static Process StartProcess(
        string project,
        string assemblyName,
        string? mode,
        string configPath,
        string? urls = null)
    {
        var start = CreateProcessStartInfo(project, assemblyName);
        if (mode is not null)
            start.ArgumentList.Add(mode);
        start.Environment[EnvironmentLoader.ConfigPathVariable] = configPath;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        if (urls is not null)
            start.Environment["ASPNETCORE_URLS"] = urls;
        return Process.Start(start)
            ?? throw new InvalidOperationException("The test process did not start.");
    }

    private static Process StartWakeProcess(string connectionPath, string triggerDirectory)
    {
        var start = CreateProcessStartInfo("mk8.email.Wake", "mk8.email.Wake.dll");
        start.Environment.Remove(EnvironmentLoader.ConfigPathVariable);
        start.Environment.Remove("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add(connectionPath);
        start.ArgumentList.Add(triggerDirectory);
        return Process.Start(start)
            ?? throw new InvalidOperationException("The Wake process did not start.");
    }

    private static Process StartWorkerLaunchingWakeProcess(
        string connectionPath,
        string configPath)
    {
        var start = CreateProcessStartInfo("mk8.email.Wake", "mk8.email.Wake.dll");
        start.Environment.Remove(EnvironmentLoader.ConfigPathVariable);
        start.Environment.Remove("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        start.ArgumentList.Add("--serve-worker");
        start.ArgumentList.Add(connectionPath);
        start.ArgumentList.Add(ProcessAssemblyPath(
            "mk8.email.Application.Worker", "mk8.email.Application.Worker.dll"));
        start.ArgumentList.Add(configPath);
        return Process.Start(start)
            ?? throw new InvalidOperationException("The container-style Wake process did not start.");
    }

    private static async Task<(int ExitCode, string Output)> RunWakeDatabaseProbeAsync(
        string connectionPath)
    {
        var start = CreateProcessStartInfo("mk8.email.Wake", "mk8.email.Wake.dll");
        start.Environment.Remove(EnvironmentLoader.ConfigPathVariable);
        start.Environment.Remove("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        start.ArgumentList.Add("--probe-db");
        start.ArgumentList.Add(connectionPath);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The Wake database probe did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert.Fail("The Wake database probe exceeded its process deadline.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string project,
        string assemblyName)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? "dotnet";
        var assembly = ProcessAssemblyPath(project, assemblyName);
        var start = new ProcessStartInfo(dotnetHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
        };
        start.ArgumentList.Add(assembly);
        return start;
    }

    private static string ProcessAssemblyPath(string project, string assemblyName)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is missing.");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var assembly = Path.Combine(
            repository, project, "bin", configuration, "net10.0", assemblyName);
        Assert.IsTrue(File.Exists(assembly), "The built process executable is missing.");
        return assembly;
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
