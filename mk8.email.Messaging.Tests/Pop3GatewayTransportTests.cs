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
using mk8.email.Contracts.Pop3;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Pop3;
using mk8.email.MailWire;
using mk8.email.Messaging;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class Pop3GatewayTransportTests
{
    [TestMethod]
    [Timeout(30_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TlsPop3CrossesRemoteWorkerAndLocksMaildropAcrossSessions scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TlsPop3CrossesRemoteWorkerAndLocksMaildropAcrossSessions()
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
            "test", "pop3-route-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "pop3-route-key");
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
        var application = new StubPop3Application();
        var workerProvider = new ServiceCollection()
            .AddSingleton<IPop3ApplicationService>(application)
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
        await using var workerProviderLifetime = workerProvider.ConfigureAwait(false);
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@pop3-test-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var transport = new GatewayApplicationTransport(
            gatewayBus,
            journal,
            new GatewayApplicationOptions(
                "gateway@pop3-test-host", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        var gatewayProvider = new ServiceCollection()
            .AddSingleton<IPop3ApplicationService>(new GatewayPop3ApplicationService(transport))
            .BuildServiceProvider();
        await using var gatewayProviderLifetime = gatewayProvider.ConfigureAwait(false);
        var port = ReservePort();
        var certificatePath = CreateCertificate();
        using var expectedCertificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
        var expectedPin = expectedCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var environment = new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "email.example.test" },
            Pop3 = new Pop3Config
            {
                EnablePop3 = true,
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
        var listener = new Pop3ServerService(
            gatewayProvider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<Pop3ServerService>.Instance,
            new PostgresPop3MaildropLeaseStore(gatewayDataSource),
            journal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        await listener.StartAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            using var firstClient = await ConnectAsync(port, timeout.Token).ConfigureAwait(false);
            using var firstTls = await StartTlsAsync(firstClient, expectedPin, timeout.Token).ConfigureAwait(false);
            await WriteLineAsync(firstTls, "USER user@example.test", timeout.Token).ConfigureAwait(false);
            Assert.IsTrue((await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false)).StartsWith("+OK", StringComparison.Ordinal));
            await WriteLineAsync(firstTls, "PASS pop3-secret", timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false), "maildrop has 1 messages", StringComparison.Ordinal);

            using var secondClient = await ConnectAsync(port, timeout.Token).ConfigureAwait(false);
            using var secondTls = await StartTlsAsync(secondClient, expectedPin, timeout.Token).ConfigureAwait(false);
            await WriteLineAsync(secondTls, "USER user@example.test", timeout.Token).ConfigureAwait(false);
            Assert.IsTrue((await ReadLineAsync(secondTls, timeout.Token).ConfigureAwait(false)).StartsWith("+OK", StringComparison.Ordinal));
            await WriteLineAsync(secondTls, "PASS pop3-secret", timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(await ReadLineAsync(secondTls, timeout.Token).ConfigureAwait(false), "[IN-USE]", StringComparison.Ordinal);

            await WriteLineAsync(firstTls, "STAT", timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false), "+OK 1 ", StringComparison.Ordinal);
            await WriteLineAsync(firstTls, "RETR 1", timeout.Token).ConfigureAwait(false);
            Assert.IsTrue((await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
            var body = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false);
                if (string.Equals(line, ".", StringComparison.Ordinal))
                    break;
                body.Add(line);
            }
            CollectionAssert.Contains(body, "..body");
            await WriteLineAsync(firstTls, "DELE 1", timeout.Token).ConfigureAwait(false);
            Assert.IsTrue((await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
            await WriteLineAsync(firstTls, "QUIT", timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(await ReadLineAsync(firstTls, timeout.Token).ConfigureAwait(false), "1 messages deleted", StringComparison.Ordinal);
            CollectionAssert.AreEqual(new[] { application.MessageId }, application.DeletedIds);

            await WriteLineAsync(secondTls, "PASS pop3-secret", timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(await ReadLineAsync(secondTls, timeout.Token).ConfigureAwait(false), "maildrop has 1 messages", StringComparison.Ordinal);
            await WriteLineAsync(secondTls, "QUIT", timeout.Token).ConfigureAwait(false);
            Assert.IsTrue((await ReadLineAsync(secondTls, timeout.Token).ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));

            var operations = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests ORDER BY created_at");
            await using var operationsLifetime = operations.ConfigureAwait(false);
            var operationReader = (await operations.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var operationReaderLifetime = operationReader.ConfigureAwait(false);
            var observed = new List<string>();
            while (await operationReader.ReadAsync(timeout.Token).ConfigureAwait(false))
                observed.Add(operationReader.GetString(0));
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3AuthenticatePassword);
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3ListMaildrop);
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3GetMessage);
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3CommitDeletes);

            var traffic = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE application_request_id IS NULL) "
                + "FROM gateway_traffic_records WHERE protocol = 'pop3'");
            await using var trafficLifetime = traffic.ConfigureAwait(false);
            var trafficReader = (await traffic.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var trafficReaderLifetime = trafficReader.ConfigureAwait(false);
            Assert.IsTrue(await trafficReader.ReadAsync(timeout.Token).ConfigureAwait(false));
            Assert.IsTrue(trafficReader.GetInt64(0) >= 8);
            Assert.IsTrue(trafficReader.GetInt64(1) >= 4);

            var ciphertext = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE protocol = 'pop3' AND payload_inline IS NOT NULL");
            await using var ciphertextLifetime = ciphertext.ConfigureAwait(false);
            var ciphertextReader = (await ciphertext.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var ciphertextReaderLifetime = ciphertextReader.ConfigureAwait(false);
            while (await ciphertextReader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                Assert.IsFalse(Encoding.UTF8.GetString(
await (ciphertextReader.GetFieldValueAsync<byte[]>(0)).ConfigureAwait(false))
                    .Contains("pop3-secret", StringComparison.Ordinal));
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

    private static async Task<SslStream> StartTlsAsync(
        TcpClient client,
        string expectedPin,
        CancellationToken cancellationToken)
    {
        var network = client.GetStream();
        Assert.IsTrue((await ReadLineAsync(network, cancellationToken).ConfigureAwait(false)).StartsWith("+OK ", StringComparison.Ordinal));
        await WriteLineAsync(network, "STLS", cancellationToken).ConfigureAwait(false);
        StringAssert.Contains(await ReadLineAsync(network, cancellationToken).ConfigureAwait(false), "Begin TLS negotiation", StringComparison.Ordinal);
        var tls = new SslStream(network, leaveInnerStreamOpen: false, (_, certificate, _, errors) =>
            certificate is not null
            && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
            && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), expectedPin, StringComparison.Ordinal));
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "email.example.test" },
            cancellationToken).ConfigureAwait(false);
        return tls;
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
        var path = Path.Combine(Path.GetTempPath(), $"mk8-pop3-{Guid.NewGuid():N}.pfx");
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
        throw new EndOfStreamException("The POP3 connection closed early.");
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string line,
        CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cancellationToken).ConfigureAwait(false);

    private sealed class StubPop3Application : IPop3ApplicationService
    {
        private static readonly byte[] Raw =
            "From: sender@example.test\nTo: user@example.test\n\n.body\n"u8.ToArray();
        public Guid UserId { get; } = Guid.CreateVersion7();
        public Guid MessageId { get; } = Guid.CreateVersion7();
        public Guid[] DeletedIds { get; private set; } = [];

        public Task<Pop3IdentityResult> AuthenticatePasswordAsync(
            Pop3PasswordAuthentication request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(request.Username, "user@example.test", StringComparison.Ordinal) && string.Equals(request.Password, "pop3-secret"
, StringComparison.Ordinal) ? new Pop3IdentityResult(UserId, request.Username)
                : new Pop3IdentityResult(null, null));

        public Task<Pop3IdentityResult> AuthenticateOAuthAsync(
            Pop3OAuthAuthentication request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Pop3MaildropSnapshot> ListMaildropAsync(
            Pop3UserRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Pop3MaildropSnapshot(
                [new Pop3MessageSummary(MessageId, 1, Pop3WireCodec.GetNormalizedCrlfLength(Raw))]));

        public Task<Pop3MessageResult> GetMessageAsync(
            Pop3MessageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Pop3MessageResult(request.MessageId == MessageId ? Raw : null));

        public Task<Pop3DeleteResult> CommitDeletesAsync(
            Pop3DeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            DeletedIds = request.MessageIds;
            return Task.FromResult(new Pop3DeleteResult(request.MessageIds.Length));
        }
    }
}
