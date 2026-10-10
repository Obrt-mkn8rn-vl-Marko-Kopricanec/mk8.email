using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Globalization;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.Configuration;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Storage;
using mk8.email.MailWire;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes the explicit full-host controls by reflection.")]
internal sealed partial class NativeHostStartupTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ProductionHostsAdmitOnlyRealLocalRecipientsAndDeliverThroughScanner(bool unknownRecipient, bool injectFailure)
    {
        Assert.IsNotNull(TestContext);
        var blob = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blob)) throw new AssertInconclusiveException("An owned Blob development endpoint is required.");
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false)
            ?? throw new AssertInconclusiveException("An owned PostgreSQL development endpoint is required.");
        DirectoryInfo? directory = null;
        BlobContainerClient? container = null;
        var hosts = new List<NativeHostProcess>();
        var scanners = new List<NativeHostScanner>();
        var injected = injectFailure ? new AssertFailedException("Controlled assertion after actual hosted startup.") : null;
        Exception? original = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("mk8-native-host-");
            container = new BlobServiceClient(blob).GetBlobContainerClient("mk8-native-host-" + Guid.NewGuid().ToString("N"));
            await RunHostsAsync(database, blob, container.Name, directory.FullName, unknownRecipient, hosts, scanners, injected).ConfigureAwait(false);
        }
        // Retain the observation fault while independently joining every owned child.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            original = exception;
        }
        await RetireAsync(hosts, scanners, database, container, directory, original).ConfigureAwait(false);
        Assert.IsTrue(hosts.TrueForAll(host => host.Retired));
        if (original is not null && !ReferenceEquals(original, injected)) ExceptionDispatchInfo.Capture(original).Throw();
        if (injected is not null) Assert.AreSame(injected, original);
    }

    private async Task RunHostsAsync(PostgresTestDatabase database, string blob, string container,
        string directory, bool unknownRecipient, List<NativeHostProcess> hosts, List<NativeHostScanner> scanners, Exception? injected,
        bool? implicitTls = null,
        Func<NativeHostProcess, NativeHostProcess, int, CancellationToken, Task>? shutdownControl = null,
        Func<int, string, Task<string>, CancellationToken, Task>? submissionControl = null)
    {
        Assert.IsNotNull(TestContext);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var smtpPort = AllocateLoopbackPort();
        var httpPort = AllocateLoopbackPort();
        var scannerPort = AllocateLoopbackPort();
        var scanner = new NativeHostScanner(scannerPort, includeSubmissionSignature: submissionControl is not null);
        scanners.Add(scanner);
        await scanner.StartAsync(token).ConfigureAwait(false);
        var config = CreateConfig(database, blob, container, directory, smtpPort, scannerPort, implicitTls);
        ValidateHostRoles(config);
        var configPath = Path.Combine(directory, "native-host.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config), token).ConfigureAwait(false);
        await SeedAsync(database.ConnectionString, token).ConfigureAwait(false);
        if (implicitTls.HasValue) await SetSubmissionPasswordAsync(database.ConnectionString, token).ConfigureAwait(false);
        {
            var prepare = NativeHostProcess.Start("MK8_EMAIL_TEST_WORKER_DLL", configPath, TestContext, "--prepare");
            hosts.Add(prepare);
            await prepare.RequireSuccessfulExitAsync(token).ConfigureAwait(false);
        }
        var worker = NativeHostProcess.Start("MK8_EMAIL_TEST_WORKER_DLL", configPath, TestContext, "--serve");
        hosts.Add(worker);
        try { await worker.RequireStartedAsync(token).ConfigureAwait(false); }
        finally { await RecordStartupPointAsync(database.ConnectionString).ConfigureAwait(false); }
        var gateway = NativeHostProcess.Start("MK8_EMAIL_TEST_GATEWAY_DLL", configPath, TestContext,
            httpUrl: string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{httpPort}"));
        hosts.Add(gateway);
        await WaitForApplicationAsync(gateway, worker, httpPort, token).ConfigureAwait(false);
        if (injected is not null) ExceptionDispatchInfo.Capture(injected).Throw();
        if (shutdownControl is not null)
        {
            await shutdownControl(worker, gateway, smtpPort, token).ConfigureAwait(false);
            return;
        }
        if (submissionControl is not null)
            await submissionControl(smtpPort, config.Tls.CertificatePath!, scanner.Scanned, token).ConfigureAwait(false);
        else if (implicitTls.HasValue)
            await RunSubmissionSessionAsync(database.ConnectionString, smtpPort, config.Tls.CertificatePath!, implicitTls.Value, token).ConfigureAwait(false);
        else
            await RunSessionAsync(database.ConnectionString, blob, container, smtpPort, unknownRecipient, scanner.Scanned, token).ConfigureAwait(false);
        AssertHostsRunning(worker, gateway);
    }

    private static void AssertHostsRunning(NativeHostProcess worker, NativeHostProcess gateway)
    {
        Assert.IsFalse(worker.HasExited, "The Worker exited before the native canary completed.");
        Assert.IsFalse(gateway.HasExited, "The Gateway exited before the native canary completed.");
    }

    private static void ValidateHostRoles(EnvironmentConfig config)
    {
        var gatewayErrors = config.Validate(isDevelopment: false, EnvironmentValidationRole.Gateway);
        var workerErrors = config.Validate(isDevelopment: false, EnvironmentValidationRole.ApplicationWorker);
        Assert.HasCount(0, gatewayErrors, string.Join("; ", gatewayErrors));
        Assert.HasCount(0, workerErrors, string.Join("; ", workerErrors));
    }

    private async Task RecordStartupPointAsync(string connectionString)
    {
        Assert.IsNotNull(TestContext);
        using var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, Timeout = 2, CommandTimeout = 2 }.ConnectionString);
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.OpenAsync(diagnosticDeadline.Token).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = "SELECT state, wait_event_type, count(*) FROM pg_stat_activity WHERE datname=current_database() GROUP BY state, wait_event_type";
            var reader = await command.ExecuteReaderAsync(diagnosticDeadline.Token).ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            var states = new List<object>();
            while (await reader.ReadAsync(diagnosticDeadline.Token).ConfigureAwait(false))
            {
                var stateMissing = await reader.IsDBNullAsync(0, diagnosticDeadline.Token).ConfigureAwait(false);
                var waitMissing = await reader.IsDBNullAsync(1, diagnosticDeadline.Token).ConfigureAwait(false);
                states.Add(new { state = stateMissing ? null : reader.GetString(0), wait = waitMissing ? null : reader.GetString(1), count = reader.GetInt64(2) });
            }
            TestContext.WriteLine("Sequential startup DB point (not a causal/atomic timeline): {0}", JsonSerializer.Serialize(states));
        }
        // Secondary nonfatal diagnostics may not replace the actual startup fault.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { TestContext.WriteLine("Startup point unavailable: {0}", error.GetType().Name); }
    }

    private static async Task RunSessionAsync(string connection, string blob, string container, int smtpPort,
        bool unknownRecipient, Task<string> scanned, CancellationToken token)
    {
        var client = await NativeSmtpClient.ConnectAsync(smtpPort, token).ConfigureAwait(false);
        await using var clientLifetime = client.ConfigureAwait(false);
        await client.GreetAsync(token).ConfigureAwait(false);
        await client.BeginMessageAsync(unknownRecipient ? "missing@example.test" : "receiver@example.test", token).ConfigureAwait(false);
        if (unknownRecipient)
        {
            Assert.StartsWith("550 ", await client.ReadAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
            await client.WriteAsync("DATA", token).ConfigureAwait(false);
            Assert.StartsWith("503 ", await client.ReadAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
            var context = OpenContext(connection);
            await using var contextLifetime = context.ConfigureAwait(false);
            Assert.AreEqual(0, await context.MailQueueMessages.CountAsync(token).ConfigureAwait(false));
            Assert.AreEqual(0, await context.Emails.CountAsync(token).ConfigureAwait(false));
            Assert.IsFalse(scanned.IsCompleted);
        }
        else
        {
            Assert.StartsWith("250 ", await client.ReadAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
            await client.WriteAsync("DATA", token).ConfigureAwait(false);
            Assert.StartsWith("354 ", await client.ReadAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
            const string raw = "From: sender@remote.test\r\nTo: receiver@example.test\r\nSubject: hosted inbound\r\n\r\noriginal body\r\n";
            await client.WriteDataAsync(raw, token).ConfigureAwait(false);
            Assert.StartsWith("250 2.0.0 Queued as ", await client.ReadAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
            var scannedRaw = await scanned.WaitAsync(token).ConfigureAwait(false);
            Assert.EndsWith(raw, scannedRaw, StringComparison.Ordinal);
            await AssertDeliveredAsync(connection, blob, container, scannedRaw, token).ConfigureAwait(false);
        }
        await client.WriteAsync("QUIT", token).ConfigureAwait(false);
        Assert.StartsWith("221 ", await client.ReadAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
    }

    private static async Task RetireAsync(List<NativeHostProcess> hosts, List<NativeHostScanner> scanners, PostgresTestDatabase database,
        BlobContainerClient? container, DirectoryInfo? directory, Exception? original)
    {
        var errors = new List<Exception>();
        foreach (var host in Enumerable.Reverse(hosts))
            await CleanupStageAsync(() => host.DisposeAsync().AsTask(), errors).ConfigureAwait(false);
        if (hosts.TrueForAll(host => host.Retired))
        {
            foreach (var scanner in scanners)
                await CleanupStageAsync(() => scanner.DisposeAsync().AsTask(), errors).ConfigureAwait(false);
        }
        if (hosts.TrueForAll(host => host.Retired) && scanners.TrueForAll(scanner => scanner.Retired))
        {
            if (container is not null)
                await CleanupStageAsync(() => container.DeleteIfExistsAsync(), errors).ConfigureAwait(false);
            await CleanupStageAsync(() => database.DisposeAsync().AsTask(), errors).ConfigureAwait(false);
            if (directory is not null)
                await CleanupStageAsync(() => { directory.Delete(recursive: true); return Task.CompletedTask; }, errors).ConfigureAwait(false);
        }
        else
        {
            errors.Add(new InvalidOperationException("Owned host work remains active; fixture dependency retirement is refused."));
        }
        if (errors.Count == 0) return;
        if (original is not null) errors.Insert(0, original);
        throw new AggregateException("Native hosted fixture retirement failed.", errors);
    }

    private static async Task CleanupStageAsync(Func<Task> cleanup, List<Exception> errors)
    {
        try { await cleanup().ConfigureAwait(false); }
        // An ordinary cleanup failure must not omit the other owned process joins.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { errors.Add(exception); }
    }

    private static EnvironmentConfig CreateConfig(PostgresTestDatabase database, string blob, string container,
        string directory, int smtpPort, int scannerPort, bool? implicitTls = null)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        // Reserved tenant identities and loopback listeners are finite owned fixtures,
        // not usable deployment defaults or allocated public endpoints.
        return new EnvironmentConfig
        {
            Database = new DatabaseConfig
            {
                Host = connection.Host!,
                Port = connection.Port,
                Name = database.DatabaseName,
                Username = connection.Username!,
                Password = string.IsNullOrEmpty(connection.Password) ? "local-host-test-only-password" : connection.Password,
            },
            Smtp = implicitTls.HasValue
                ? new SmtpConfig
                {
                    Hostname = "email.example.test",
                    ListenAddress = "127.0.0.1",
                    EnableSmtp = false,
                    EnableSubmission = !implicitTls.Value,
                    SubmissionPort = smtpPort,
                    EnableImplicitTls = implicitTls.Value,
                    ImplicitTlsPort = smtpPort,
                    EnableStartTls = !implicitTls.Value,
                    RequireTls = true,
                    RequireAuth = true,
                    AllowRelay = false,
                }
                : new SmtpConfig { Hostname = "email.example.test", ListenAddress = "127.0.0.1", Port = smtpPort, AllowRelay = false },
            Tls = implicitTls.HasValue ? new TlsConfig { CertificatePath = CreateSubmissionCertificate(directory) } : new TlsConfig(),
            Imap = new ImapConfig { EnableImap = false },
            Pop3 = new Pop3Config { EnablePop3 = false },
            Sieve = new SieveConfig { EnableManageSieve = false },
            Jmap = new JmapConfig { EnableJmap = false, IsDefault = false },
            Dav = new DavConfig { EnableDav = false },
            OAuth = new OAuthConfig { EnableOAuth = false },
            Filtering = new FilteringConfig { RspamdEndpoint = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{scannerPort}/checkv2") },
            Limits = new LimitsConfig { MaxMessageSizeBytes = 65_536 },
            Admin = new AdminConfig
            {
                AllowedNetworks = ["127.0.0.0/8"],
                DataProtectionKeyPath = Path.Combine(directory, "keys"),
                AuditLogPath = Path.Combine(directory, "audit.jsonl"),
                HealthStatusPath = Path.Combine(directory, "status.json"),
            },
            Messaging = new MessagingConfig
            {
                Enabled = true,
                EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                NotificationFallbackSeconds = 1,
            },
            ObjectStorage = new ObjectStorageConfig { ConnectionString = blob, ContainerName = container, CreateContainerIfMissing = true },
        };
    }

    private static EmailDbContext OpenContext(string connection) =>
        new(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(connection).Options);

    private static async Task SeedAsync(string connection, CancellationToken cancellationToken)
    {
        var context = OpenContext(connection);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "Owned hosted SMTP fixture", IsActive = true };
        var address = new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", Company = company, IsActive = true };
        var user = new UserDB { Id = Guid.CreateVersion7(), Username = "receiver@example.test", PasswordHash = "unused-anonymous-fixture", Company = company, IsActive = true };
        var inbox = new InboxDB { Id = Guid.CreateVersion7(), Name = "receiver", Address = address, Owner = user };
        inbox.Folders.Add(new FolderDB { Id = Guid.CreateVersion7(), Name = DefaultFolders.Inbox, JmapRole = "inbox" });
        await context.Inboxes.AddAsync(inbox, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertDeliveredAsync(string connection, string blob, string container, string scanned,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = OpenContext(connection);
            await using var contextLifetime = context.ConfigureAwait(false);
            var queue = await context.MailQueueMessages.AsNoTracking().Include(item => item.Recipients).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(queue.State, MailQueueStates.Completed, StringComparison.Ordinal))
            {
                var email = await context.Emails.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(MailQueueDirections.Inbound, queue.Direction, StringComparer.Ordinal);
                Assert.IsNull(queue.AuthenticatedUser);
                Assert.AreEqual("no action", queue.ScanAction, StringComparer.Ordinal);
                Assert.AreEqual(queue.Recipients.Single().Id, email.QueueDeliveryId);
                Assert.AreEqual(1, email.Uid);
                Assert.IsGreaterThan(0L, email.ModSeq);
                Assert.AreEqual(LargeObjectProviders.AzureBlob, email.RawMessageObjectProvider, StringComparer.Ordinal);
                Assert.AreEqual(MailQueueRecipientStates.Delivered, queue.Recipients.Single().State, StringComparer.Ordinal);
                var store = AzureBlobLargeObjectStore.FromConnectionString(blob, new AzureBlobLargeObjectStoreOptions { ContainerName = container });
                var queuedReference = new LargeObjectReference(LargeObjectProviders.AzureBlob, queue.RawMessageObjectName!,
                    queue.RawMessageSizeBytes, queue.RawMessageObjectSha256!, queue.RawMessageObjectEntityTag!);
                var mailboxReference = new LargeObjectReference(LargeObjectProviders.AzureBlob, email.RawMessageObjectName!,
                    email.SizeBytes, email.RawMessageObjectSha256!, email.RawMessageObjectEntityTag!);
                var queuedBytes = new MemoryStream();
                await using var queuedLifetime = queuedBytes.ConfigureAwait(false);
                var mailboxBytes = new MemoryStream();
                await using var mailboxLifetime = mailboxBytes.ConfigureAwait(false);
                await store.CopyToAsync(queuedReference, queuedBytes, cancellationToken).ConfigureAwait(false);
                await store.CopyToAsync(mailboxReference, mailboxBytes, cancellationToken).ConfigureAwait(false);
                Assert.AreSequenceEqual(MailWireEncoding.Instance.GetBytes(scanned), queuedBytes.ToArray());
                Assert.AreSequenceEqual(MailWireEncoding.Instance.GetBytes(queue.AddedHeaders + scanned), mailboxBytes.ToArray());
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForApplicationAsync(NativeHostProcess gateway, NativeHostProcess worker, int port, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/health/application"));
        while (true)
        {
            Assert.IsFalse(gateway.HasExited, "The Gateway exited during hosted startup.");
            Assert.IsFalse(worker.HasExited, "The Worker exited during hosted startup.");
            try
            {
                using var response = await client.GetAsync(uri, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException)
            {
                // The owned host may not yet have bound its loopback listener.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    private static int AllocateLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
