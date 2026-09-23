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
public sealed class DistributedProcessBoundaryTests
{
    [TestMethod]
    public async Task GatewayJournalsWhileWorkerIsStoppedAndWakeTriggersLaterDispatchAcrossProcesses()
    {
        await using var database = await RequirePostgresAsync();
        var blobConnection = RequireAzureBlobConnection();
        var container = new BlobServiceClient(blobConnection)
            .GetBlobContainerClient("mk8-process-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateTempSubdirectory("mk8-process-boundary-");
        try
        {
            await using (var context = new EmailDbContext(
                             new DbContextOptionsBuilder<EmailDbContext>()
                                 .UseNpgsql(database.ConnectionString).Options))
            {
                await context.Database.EnsureCreatedAsync();
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
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));

            var prepared = await RunWorkerAsync("--prepare", configPath);
            Assert.AreEqual(0, prepared.ExitCode, prepared.Output);

            var wakeRole = $"mk8_process_wake_{Guid.NewGuid():N}";
            var wakePassword = Guid.NewGuid().ToString("N");
            var gatewayRole = $"mk8_process_gateway_{Guid.NewGuid():N}";
            var gatewayPassword = Guid.NewGuid().ToString("N");
            await using var roleAdmin = new NpgsqlConnection(database.ConnectionString);
            await roleAdmin.OpenAsync();
            await CreateRestrictedWakeRoleAsync(
                roleAdmin, database.DatabaseName, wakeRole, wakePassword);
            var gatewayRoleCreated = false;
            try
            {
                await CreateRestrictedGatewayRoleAsync(
                    roleAdmin, database.DatabaseName, gatewayRole, gatewayPassword);
                gatewayRoleCreated = true;
                var gatewayConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
                {
                    Username = gatewayRole,
                    Password = gatewayPassword,
                };
                await using (var gatewayDataSource = NpgsqlDataSource.Create(
                                 gatewayConnection.ConnectionString))
                {
                    await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource);
                    await using var applicationTable = gatewayDataSource.CreateCommand(
                        "SELECT id FROM users");
                    var denial = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => applicationTable.ExecuteScalarAsync());
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, denial.SqlState);
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
                await File.WriteAllTextAsync(gatewayConfigPath, gatewayConfig.ToJsonString());

                var wakeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
                {
                    Username = wakeRole,
                    Password = wakePassword,
                    Pooling = false,
                };
                var wakeConnectionPath = Path.Combine(directory.FullName, "wake-connection.txt");
                await File.WriteAllTextAsync(wakeConnectionPath, wakeConnection.ConnectionString);
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
                    await WaitForGatewayAsync(client, gateway, TimeSpan.FromSeconds(15));

                    Assert.AreEqual(HttpStatusCode.OK,
                        (await client.GetAsync("/health/live")).StatusCode);
                    Assert.AreEqual(HttpStatusCode.OK,
                        (await client.GetAsync("/health/ready")).StatusCode);
                    Assert.AreEqual(HttpStatusCode.Unauthorized,
                        (await client.GetAsync("/.well-known/jmap")).StatusCode);

                    await using var inspection = new NpgsqlConnection(database.ConnectionString);
                    await inspection.OpenAsync();
                    Assert.AreEqual(2, await CountAsync(
                        inspection,
                        "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'jmap'"));

                    // The reverse presentation lane remains usable without an Application
                    // process and without granting Gateway access to Application tables.
                    await using (var applicationDataSource = NpgsqlDataSource.Create(
                                     database.ConnectionString))
                    using (var protector = new AesGcmPayloadProtector(new MessagingEncryptionKey(
                               config.Messaging.EncryptionKeyId,
                               Convert.FromBase64String(config.Messaging.EncryptionKey))))
                    {
                        var presentation = new JmapPushPresentationClient(
                            new PostgresPresentationBus(applicationDataSource, protector),
                            NullLogger<JmapPushPresentationClient>.Instance);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        Assert.IsFalse(await presentation.IsSafeUrlAsync(
                            "https://127.0.0.1/push", timeout.Token));
                    }
                    Assert.AreEqual(1, await CountAsync(
                        inspection,
                        "SELECT count(*) FROM presentation_requests "
                        + "WHERE protocol = 'webpush' AND state = 'completed'"));
                    Assert.AreEqual(2, await CountAsync(
                        inspection,
                        "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'webpush'"));

                    var pendingProbe = client.GetAsync("/health/application");
                    await WaitForQueuedProbeAsync(inspection, TimeSpan.FromSeconds(10));
                    Assert.IsFalse(gateway.HasExited);
                    Assert.IsFalse(pendingProbe.IsCompleted);
                    Assert.AreEqual(HttpStatusCode.OK,
                        (await client.GetAsync("/health/live")).StatusCode);

                    using var wake = StartWakeProcess(wakeConnectionPath, triggerDirectory);
                    var wakeOutput = wake.StandardOutput.ReadToEndAsync();
                    var wakeErrors = wake.StandardError.ReadToEndAsync();
                    try
                    {
                        var trigger = Path.Combine(triggerDirectory, "trigger");
                        await WaitForWakeTriggerAsync(trigger, wake, TimeSpan.FromSeconds(15));
                        Assert.IsFalse(pendingProbe.IsCompleted);
                        Assert.AreEqual(1, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM application_requests "
                            + "WHERE protocol = 'health' AND state = 'pending'"));
                        File.Delete(trigger); // The path unit consumes the signal before --drain.

                        var drained = await RunWorkerAsync("--drain", configPath);
                        Assert.AreEqual(0, drained.ExitCode, drained.Output);
                        Assert.AreEqual(HttpStatusCode.OK, (await pendingProbe).StatusCode);
                        Assert.AreEqual(1, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM application_requests "
                            + "WHERE protocol = 'health' AND state = 'completed'"));
                        Assert.AreEqual(2, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'health'"));
                        Assert.IsFalse(gateway.HasExited);
                        Assert.IsFalse(wake.HasExited);
                    }
                    finally
                    {
                        if (!wake.HasExited)
                            wake.Kill(entireProcessTree: true);
                        await wake.WaitForExitAsync();
                        await Task.WhenAll(wakeOutput, wakeErrors);
                    }

                    var shallowProbe = await RunWakeDatabaseProbeAsync(wakeConnectionPath);
                    Assert.AreEqual(0, shallowProbe.ExitCode, shallowProbe.Output);
                    StringAssert.Contains(shallowProbe.Output, "due=false");

                    using var containerWake = StartWorkerLaunchingWakeProcess(
                        wakeConnectionPath, configPath);
                    var containerWakeOutput = containerWake.StandardOutput.ReadToEndAsync();
                    var containerWakeErrors = containerWake.StandardError.ReadToEndAsync();
                    try
                    {
                        Assert.AreEqual(HttpStatusCode.OK,
                            (await client.GetAsync("/health/application")).StatusCode);
                        Assert.AreEqual(2, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM application_requests "
                            + "WHERE protocol = 'health' AND state = 'completed'"));
                        Assert.AreEqual(4, await CountAsync(
                            inspection,
                            "SELECT count(*) FROM gateway_traffic_records WHERE protocol = 'health'"));
                        Assert.IsFalse(containerWake.HasExited);
                        Assert.IsFalse(gateway.HasExited);
                    }
                    finally
                    {
                        if (!containerWake.HasExited)
                            containerWake.Kill(entireProcessTree: true);
                        await containerWake.WaitForExitAsync();
                        await Task.WhenAll(containerWakeOutput, containerWakeErrors);
                    }
                }
                finally
                {
                    if (!gateway.HasExited)
                        gateway.Kill(entireProcessTree: true);
                    await gateway.WaitForExitAsync();
                    await Task.WhenAll(gatewayOutput, gatewayErrors);
                }
            }
            finally
            {
                await using var cleanup = roleAdmin.CreateCommand();
                cleanup.CommandText = gatewayRoleCreated
                    ? $"""
                        DROP OWNED BY "{gatewayRole}";
                        DROP ROLE "{gatewayRole}";
                        DROP OWNED BY "{wakeRole}";
                        DROP ROLE "{wakeRole}";
                        """
                    : $"DROP OWNED BY \"{wakeRole}\"; DROP ROLE \"{wakeRole}\"";
                await cleanup.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await container.DeleteIfExistsAsync();
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PendingRestoreKeepsGatewayLiveButBlocksTrafficUntilItIsComplete()
    {
        await using var database = await RequirePostgresAsync();
        var directory = Directory.CreateTempSubdirectory("mk8-pending-restore-process-");
        try
        {
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE public.mk8_restore_state (
                        id smallint PRIMARY KEY,
                        state text NOT NULL);
                    INSERT INTO public.mk8_restore_state VALUES (1, 'pending');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var config = CreateGatewayTestConfig(
                new NpgsqlConnectionStringBuilder(database.ConnectionString), directory);
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.Gateway));
            Assert.HasCount(0, config.Validate(false, EnvironmentValidationRole.ApplicationWorker));
            Directory.CreateDirectory(config.Admin.DataProtectionKeyPath);
            var configPath = Path.Combine(directory.FullName, "distributed.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));

            foreach (var mode in new[] { "--prepare", "--drain" })
            {
                var worker = await RunWorkerAsync(mode, configPath);
                Assert.AreEqual(1, worker.ExitCode, worker.Output);
                StringAssert.Contains(worker.Output, "restore is incomplete");
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
                await WaitForGatewayAsync(client, gateway, TimeSpan.FromSeconds(15));
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/ready")).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/.well-known/jmap")).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/application")).StatusCode);

                await using var inspection = new NpgsqlConnection(database.ConnectionString);
                await inspection.OpenAsync();
                Assert.AreEqual(0L, await CountAsync(
                    inspection,
                    "SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.users')"));

                // A restore can recreate messaging tables while its marker is still pending.
                await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
                    await PostgresMessagingSchema.EnsureAsync(dataSource);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/ready")).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/.well-known/jmap")).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/application")).StatusCode);
                Assert.AreEqual(0L, await CountAsync(
                    inspection, "SELECT count(*) FROM gateway_traffic_records"));
                Assert.AreEqual(0L, await CountAsync(
                    inspection, "SELECT count(*) FROM application_requests"));

                await using (var complete = inspection.CreateCommand())
                {
                    complete.CommandText =
                        "UPDATE public.mk8_restore_state SET state = 'complete' WHERE id = 1";
                    Assert.AreEqual(1, await complete.ExecuteNonQueryAsync());
                }
                Assert.AreEqual(HttpStatusCode.OK,
                    (await client.GetAsync("/health/ready")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized,
                    (await client.GetAsync("/.well-known/jmap")).StatusCode);
                Assert.AreEqual(2L, await CountAsync(
                    inspection, "SELECT count(*) FROM gateway_traffic_records"));
                Assert.IsFalse(gateway.HasExited);
            }
            finally
            {
                if (!gateway.HasExited)
                    gateway.Kill(entireProcessTree: true);
                await gateway.WaitForExitAsync();
                await Task.WhenAll(gatewayOutput, gatewayErrors);
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
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));

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
                await WaitForGatewayAsync(client, gateway, TimeSpan.FromSeconds(15));
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/ready")).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/.well-known/jmap")).StatusCode);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/application")).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await client.GetAsync("/health/live")).StatusCode);
                Assert.IsFalse(gateway.HasExited);
            }
            finally
            {
                if (!gateway.HasExited)
                    gateway.Kill(entireProcessTree: true);
                await gateway.WaitForExitAsync();
                await Task.WhenAll(gatewayOutput, gatewayErrors);
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
                using var response = await client.GetAsync("/health/live", cancellation.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100, cancellation.Token);
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
                    + "WHERE protocol = 'health' AND state = 'pending'") > 0)
                return;
            await Task.Delay(100, cancellation.Token);
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
            await Task.Delay(100, cancellation.Token);
        }
        Assert.Fail("Wake did not signal queued work within its deadline.");
    }

    private static async Task CreateRestrictedWakeRoleAsync(
        NpgsqlConnection admin,
        string databaseName,
        string role,
        string password)
    {
        await using var transaction = await admin.BeginTransactionAsync();
        await using var setup = admin.CreateCommand();
        setup.Transaction = transaction;
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
        await setup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task CreateRestrictedGatewayRoleAsync(
        NpgsqlConnection admin,
        string databaseName,
        string role,
        string password)
    {
        await using var transaction = await admin.BeginTransactionAsync();
        await using var setup = admin.CreateCommand();
        setup.Transaction = transaction;
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
        await setup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync()
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
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"Worker {mode} exceeded its process deadline.");
        }
        return (process.ExitCode, await output + await errors);
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
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("The Wake database probe exceeded its process deadline.");
        }
        return (process.ExitCode, await output + await errors);
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
        var database = await PostgresTestDatabase.TryCreateAsync();
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
