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
public sealed class Pop3GatewayTransportTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task TlsPop3CrossesRemoteWorkerAndLocksMaildropAcrossSessions()
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
        await using var workerProvider = new ServiceCollection()
            .AddSingleton<IPop3ApplicationService>(application)
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
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
        await using var gatewayProvider = new ServiceCollection()
            .AddSingleton<IPop3ApplicationService>(new GatewayPop3ApplicationService(transport))
            .BuildServiceProvider();
        var port = ReservePort();
        var certificatePath = CreateCertificate();
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
        await worker.StartAsync(timeout.Token);
        await listener.StartAsync(timeout.Token);
        try
        {
            using var firstClient = await ConnectAsync(port, timeout.Token);
            using var firstTls = await StartTlsAsync(firstClient, timeout.Token);
            await WriteLineAsync(firstTls, "USER user@example.test", timeout.Token);
            Assert.IsTrue((await ReadLineAsync(firstTls, timeout.Token)).StartsWith("+OK", StringComparison.Ordinal));
            await WriteLineAsync(firstTls, "PASS pop3-secret", timeout.Token);
            StringAssert.Contains(await ReadLineAsync(firstTls, timeout.Token), "maildrop has 1 messages");

            using var secondClient = await ConnectAsync(port, timeout.Token);
            using var secondTls = await StartTlsAsync(secondClient, timeout.Token);
            await WriteLineAsync(secondTls, "USER user@example.test", timeout.Token);
            Assert.IsTrue((await ReadLineAsync(secondTls, timeout.Token)).StartsWith("+OK", StringComparison.Ordinal));
            await WriteLineAsync(secondTls, "PASS pop3-secret", timeout.Token);
            StringAssert.Contains(await ReadLineAsync(secondTls, timeout.Token), "[IN-USE]");

            await WriteLineAsync(firstTls, "STAT", timeout.Token);
            StringAssert.Contains(await ReadLineAsync(firstTls, timeout.Token), "+OK 1 ");
            await WriteLineAsync(firstTls, "RETR 1", timeout.Token);
            Assert.IsTrue((await ReadLineAsync(firstTls, timeout.Token)).StartsWith("+OK ", StringComparison.Ordinal));
            var body = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync(firstTls, timeout.Token);
                if (line == ".")
                    break;
                body.Add(line);
            }
            CollectionAssert.Contains(body, "..body");
            await WriteLineAsync(firstTls, "DELE 1", timeout.Token);
            Assert.IsTrue((await ReadLineAsync(firstTls, timeout.Token)).StartsWith("+OK ", StringComparison.Ordinal));
            await WriteLineAsync(firstTls, "QUIT", timeout.Token);
            StringAssert.Contains(await ReadLineAsync(firstTls, timeout.Token), "1 messages deleted");
            CollectionAssert.AreEqual(new[] { application.MessageId }, application.DeletedIds);

            await WriteLineAsync(secondTls, "PASS pop3-secret", timeout.Token);
            StringAssert.Contains(await ReadLineAsync(secondTls, timeout.Token), "maildrop has 1 messages");
            await WriteLineAsync(secondTls, "QUIT", timeout.Token);
            Assert.IsTrue((await ReadLineAsync(secondTls, timeout.Token)).StartsWith("+OK ", StringComparison.Ordinal));

            await using var operations = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests ORDER BY created_at");
            await using var operationReader = await operations.ExecuteReaderAsync(timeout.Token);
            var observed = new List<string>();
            while (await operationReader.ReadAsync(timeout.Token))
                observed.Add(operationReader.GetString(0));
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3AuthenticatePassword);
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3ListMaildrop);
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3GetMessage);
            CollectionAssert.Contains(observed, ApplicationOperations.Pop3CommitDeletes);

            await using var traffic = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE application_request_id IS NULL) "
                + "FROM gateway_traffic_records WHERE protocol = 'pop3'");
            await using var trafficReader = await traffic.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await trafficReader.ReadAsync(timeout.Token));
            Assert.IsTrue(trafficReader.GetInt64(0) >= 8);
            Assert.IsTrue(trafficReader.GetInt64(1) >= 4);

            await using var ciphertext = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE protocol = 'pop3' AND payload_inline IS NOT NULL");
            await using var ciphertextReader = await ciphertext.ExecuteReaderAsync(timeout.Token);
            while (await ciphertextReader.ReadAsync(timeout.Token))
            {
                Assert.IsFalse(Encoding.UTF8.GetString(
                    ciphertextReader.GetFieldValue<byte[]>(0))
                    .Contains("pop3-secret", StringComparison.Ordinal));
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

    private static async Task<SslStream> StartTlsAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var network = client.GetStream();
        Assert.IsTrue((await ReadLineAsync(network, cancellationToken)).StartsWith("+OK ", StringComparison.Ordinal));
        await WriteLineAsync(network, "STLS", cancellationToken);
        StringAssert.Contains(await ReadLineAsync(network, cancellationToken), "Begin TLS negotiation");
        var tls = new SslStream(network, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "email.example.test" },
            cancellationToken);
        return tls;
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
        throw new EndOfStreamException("The POP3 connection closed early.");
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string line,
        CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cancellationToken);

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
            Task.FromResult(request.Username == "user@example.test" && request.Password == "pop3-secret"
                ? new Pop3IdentityResult(UserId, request.Username)
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
