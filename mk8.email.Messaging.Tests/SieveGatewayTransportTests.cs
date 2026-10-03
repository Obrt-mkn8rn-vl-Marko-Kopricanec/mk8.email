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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class SieveGatewayTransportTests
{
    [TestMethod]
    [Timeout(30_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TlsSieveAuthenticationCrossesRemoteWorkerAndRecordsWireTraffic scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TlsSieveAuthenticationCrossesRemoteWorkerAndRecordsWireTraffic()
    {
        var database = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var databaseLifetime = new NullableAsyncDisposable(database).ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSourceLifetime = workerDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
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
        var workerProvider = new ServiceCollection()
            .AddSingleton<ISieveApplicationService>(application)
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
        await using var workerProviderLifetime = workerProvider.ConfigureAwait(false);
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
        var gatewayProvider = new ServiceCollection()
            .AddSingleton<ISieveApplicationService>(new GatewaySieveApplicationService(transport))
            .BuildServiceProvider();
        await using var gatewayProviderLifetime = gatewayProvider.ConfigureAwait(false);
        var port = ReservePort();
        var certificatePath = CreateCertificate();
        using var expectedCertificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
        var expectedPin = expectedCertificate.GetCertHashString(HashAlgorithmName.SHA256);
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
        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        await listener.StartAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            using var client = await ConnectAsync(port, timeout.Token).ConfigureAwait(false);
            var network = client.GetStream();
            await ReadCapabilitiesAsync(network, timeout.Token).ConfigureAwait(false);
            await WriteLineAsync(network, "STARTTLS", timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(await ReadLineAsync(network, timeout.Token).ConfigureAwait(false), "Begin TLS negotiation", StringComparison.Ordinal);

            using var tls = new SslStream(network, false, (_, certificate, _, errors) =>
                certificate is not null
                && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
                && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), expectedPin, StringComparison.Ordinal));
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "email.example.test" },
                timeout.Token).ConfigureAwait(false);
            await ReadCapabilitiesAsync(tls, timeout.Token).ConfigureAwait(false);
            var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "\0user@example.test\0sieve-secret"));
            await WriteLineAsync(tls, $"AUTHENTICATE \"PLAIN\" \"{plain}\"", timeout.Token).ConfigureAwait(false);
            Assert.IsTrue((await ReadLineAsync(tls, timeout.Token).ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
            await WriteLineAsync(tls, "LISTSCRIPTS", timeout.Token).ConfigureAwait(false);
            Assert.AreEqual("\"primary\" ACTIVE", await ReadLineAsync(tls, timeout.Token).ConfigureAwait(false), StringComparer.Ordinal);
            Assert.IsTrue((await ReadLineAsync(tls, timeout.Token).ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
            Assert.AreEqual("sieve-secret", application.Password?.Password, StringComparer.Ordinal);

            var operations = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests ORDER BY created_at");
            await using var operationsLifetime = operations.ConfigureAwait(false);
            var operationReader = (await operations.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var operationReaderLifetime = operationReader.ConfigureAwait(false);
            var observed = new List<string>();
            while (await operationReader.ReadAsync(timeout.Token).ConfigureAwait(false))
                observed.Add(operationReader.GetString(0));
            CollectionAssert.AreEqual(
                new[] { ApplicationOperations.SieveAuthenticatePassword, ApplicationOperations.SieveList },
                observed);

            var traffic = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE application_request_id IS NULL) "
                + "FROM gateway_traffic_records WHERE protocol = 'sieve'");
            await using var trafficLifetime = traffic.ConfigureAwait(false);
            var trafficReader = (await traffic.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var trafficReaderLifetime = trafficReader.ConfigureAwait(false);
            Assert.IsTrue(await trafficReader.ReadAsync(timeout.Token).ConfigureAwait(false));
            Assert.IsTrue(trafficReader.GetInt64(0) >= 6);
            Assert.IsTrue(trafficReader.GetInt64(1) >= 4);

            var ciphertext = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE protocol = 'sieve' AND payload_inline IS NOT NULL");
            await using var ciphertextLifetime = ciphertext.ConfigureAwait(false);
            var ciphertextReader = (await ciphertext.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var ciphertextReaderLifetime = ciphertextReader.ConfigureAwait(false);
            while (await ciphertextReader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                Assert.IsFalse(Encoding.UTF8.GetString(
await (ciphertextReader.GetFieldValueAsync<byte[]>(0)).ConfigureAwait(false))
                    .Contains("sieve-secret", StringComparison.Ordinal));
            }
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None).ConfigureAwait(false);
            listener.Dispose();
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            worker.Dispose();
            File.Delete(certificatePath);
        }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
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
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReadCapabilitiesAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!(await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal))
        {
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false) != 0)
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
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cancellationToken).ConfigureAwait(false);

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
