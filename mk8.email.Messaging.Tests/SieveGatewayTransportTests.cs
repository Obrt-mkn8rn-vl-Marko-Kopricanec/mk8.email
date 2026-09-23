using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Sieve;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Sieve;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class SieveGatewayTransportTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task TlsSieveAuthenticationCrossesRemoteWorkerAndRecordsWireTraffic()
    {
        await using var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "sieve-route-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "sieve-route-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gatewayBus = new PostgresApplicationBus(
            gatewayDataSource, gatewayProtector, options);
        var workerBus = new PostgresApplicationBus(
            workerDataSource, workerProtector, options);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource, gatewayProtector, options);

        var application = new StubSieveApplication();
        await using var workerProvider = new ServiceCollection()
            .AddSingleton<ISieveApplicationService>(application)
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@sieve-test-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var transport = new GatewayApplicationTransport(
            gatewayBus,
            journal,
            new GatewayApplicationOptions(
                "gateway@sieve-test-host", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        await using var gatewayProvider = new ServiceCollection()
            .AddSingleton<ISieveApplicationService>(new GatewaySieveApplicationService(transport))
            .BuildServiceProvider();
        var port = ReservePort();
        var certificatePath = CreateCertificate();
        var environment = new EnvironmentConfig
        {
            Sieve = new SieveConfig
            {
                EnableManageSieve = true,
                EnableStartTls = true,
                Port = port,
            },
            Tls = new TlsConfig { CertificatePath = certificatePath },
            Limits = new LimitsConfig
            {
                ConnectionTimeoutSeconds = 15,
                MaxConnectionsPerIp = 10,
            },
        };
        var listener = new ManageSieveServerService(
            gatewayProvider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<ManageSieveServerService>.Instance,
            journal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await worker.StartAsync(timeout.Token);
        await listener.StartAsync(timeout.Token);
        try
        {
            using var client = await ConnectAsync(port, timeout.Token);
            var network = client.GetStream();
            await ReadCapabilitiesAsync(network, timeout.Token);
            await WriteLineAsync(network, "STARTTLS", timeout.Token);
            StringAssert.Contains(await ReadLineAsync(network, timeout.Token), "Begin TLS negotiation");

            using var tls = new SslStream(network, false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "email.example.test" },
                timeout.Token);
            await ReadCapabilitiesAsync(tls, timeout.Token);
            var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "\0user@example.test\0sieve-secret"));
            await WriteLineAsync(tls, $"AUTHENTICATE \"PLAIN\" \"{plain}\"", timeout.Token);
            Assert.IsTrue((await ReadLineAsync(tls, timeout.Token)).StartsWith("OK ", StringComparison.Ordinal));
            await WriteLineAsync(tls, "LISTSCRIPTS", timeout.Token);
            Assert.AreEqual("\"primary\" ACTIVE", await ReadLineAsync(tls, timeout.Token));
            Assert.IsTrue((await ReadLineAsync(tls, timeout.Token)).StartsWith("OK ", StringComparison.Ordinal));
            Assert.AreEqual("sieve-secret", application.Password?.Password);

            await using var operations = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests ORDER BY created_at");
            await using var operationReader = await operations.ExecuteReaderAsync(timeout.Token);
            var observed = new List<string>();
            while (await operationReader.ReadAsync(timeout.Token))
                observed.Add(operationReader.GetString(0));
            CollectionAssert.AreEqual(
                new[] { ApplicationOperations.SieveAuthenticatePassword, ApplicationOperations.SieveList },
                observed);

            await using var traffic = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE application_request_id IS NULL) "
                + "FROM gateway_traffic_records WHERE protocol = 'sieve'");
            await using var trafficReader = await traffic.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await trafficReader.ReadAsync(timeout.Token));
            Assert.IsTrue(trafficReader.GetInt64(0) >= 6);
            Assert.IsTrue(trafficReader.GetInt64(1) >= 4);

            await using var ciphertext = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE protocol = 'sieve' AND payload_inline IS NOT NULL");
            await using var ciphertextReader = await ciphertext.ExecuteReaderAsync(timeout.Token);
            while (await ciphertextReader.ReadAsync(timeout.Token))
            {
                Assert.IsFalse(Encoding.UTF8.GetString(
                    ciphertextReader.GetFieldValue<byte[]>(0))
                    .Contains("sieve-secret", StringComparison.Ordinal));
            }
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            File.Delete(certificatePath);
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=email.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("email.example.test");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), $"mk8-sieve-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return path;
    }

    private static async Task<TcpClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private static async Task ReadCapabilitiesAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!(await ReadLineAsync(stream, cancellationToken)).StartsWith("OK ", StringComparison.Ordinal))
        {
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (await stream.ReadAsync(single, cancellationToken) != 0)
        {
            if (single[0] == '\n')
            {
                Assert.IsTrue(bytes.Count > 0 && bytes[^1] == '\r');
                bytes.RemoveAt(bytes.Count - 1);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add(single[0]);
        }
        throw new EndOfStreamException("The ManageSieve connection closed early.");
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string line,
        CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cancellationToken);

    private sealed class StubSieveApplication : ISieveApplicationService
    {
        public SievePasswordAuthentication? Password { get; private set; }

        public Task<SieveIdentityResult> AuthenticatePasswordAsync(
            SievePasswordAuthentication request,
            CancellationToken cancellationToken = default)
        {
            Password = request;
            return Task.FromResult(new SieveIdentityResult(Guid.CreateVersion7(), request.Username));
        }

        public Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
            SieveUserRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SieveScriptSummary>>(
                [new SieveScriptSummary("primary", true, DateTime.UnixEpoch, DateTime.UnixEpoch)]);

        public Task<SieveIdentityResult> AuthenticateOAuthAsync(
            SieveOAuthAuthentication request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveScriptOperationResult> CheckSpaceAsync(
            SieveCheckSpaceRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveStoredScriptResult> GetAsync(
            SieveNamedRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveScriptOperationResult> PutAsync(
            SievePutRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveScriptOperationResult> SetActiveAsync(
            SieveSetActiveRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveScriptOperationResult> DeleteAsync(
            SieveNamedRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveScriptOperationResult> RenameAsync(
            SieveRenameRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SieveValidationResult> ValidateAsync(
            SieveValidationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
