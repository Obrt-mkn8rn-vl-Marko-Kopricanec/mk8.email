using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Sieve;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Sieve;
using mk8.email.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ManageSieveProtocolTests
{
    private const string TestUsername = "user@mk8n.com";
    private const string TestPassword = "correct horse battery staple";
    private const string TestAccessToken = "sieve-access-token";
    private static readonly Guid TestUserId = Guid.Parse("01994f34-9776-7d2d-898c-d0273d6832ef");
    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestMethod]
    [Timeout(10_000)]
    public async Task GatewaySievePresentationJournalsWireBytesAndFailsClosed()
    {
        var port = ReservePort();
        var journal = new RecordingSieveJournal();
        await using (var server = await ServerFixture.StartAsync(
                         CreateEnvironment(port), port, journal))
        {
            await using var connection = await ProtocolConnection.ConnectAsync(port);
            await connection.ReadCapabilityResponseAsync();
            await connection.WriteLineAsync("NOOP");
            Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
        }

        var sessionId = journal.Records.Single(record =>
            record.Direction == GatewayTrafficDirections.Inbound
            && Encoding.UTF8.GetString(record.Payload).Contains("NOOP\r\n", StringComparison.Ordinal))
            .SessionId;
        var records = journal.Records.Where(record => record.SessionId == sessionId).ToArray();
        Assert.IsTrue(records.Any(record => record.Direction == GatewayTrafficDirections.Outbound
            && Encoding.UTF8.GetString(record.Payload).Contains("\"IMPLEMENTATION\"", StringComparison.Ordinal)));
        Assert.IsTrue(records.All(record => record.Protocol == "sieve"));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, records.Length).Select(value => (long)value).ToArray(),
            records.Select(record => record.Sequence).ToArray());

        var rejectedPort = ReservePort();
        await using var rejectedServer = await ServerFixture.StartAsync(
            CreateEnvironment(rejectedPort),
            rejectedPort,
            new RecordingSieveJournal { RejectWrites = true });
        await using var rejectedConnection = await ProtocolConnection.ConnectAsync(rejectedPort);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => rejectedConnection.ReadLineAsync());
    }

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-sieve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = TestCertificateFactory.Create(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ClearTextSessionAdvertisesStartTlsAndRejectsAuthentication()
    {
        var port = ReservePort();
        await using var server = await ServerFixture.StartAsync(CreateEnvironment(port), port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        var capabilities = await connection.ReadCapabilityResponseAsync();
        CollectionAssert.Contains(capabilities, "\"VERSION\" \"1.0\"");
        CollectionAssert.Contains(capabilities, "\"SASL\" \"\"");
        CollectionAssert.Contains(capabilities, "\"STARTTLS\"");
        Assert.IsTrue(capabilities.Any(line => line.StartsWith("\"SIEVE\" ", StringComparison.Ordinal)));
        Assert.IsTrue(capabilities[^1].StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync($"AUTHENTICATE \"PLAIN\" \"{PlainCredentials()}\"");
        StringAssert.Contains(await connection.ReadLineAsync(), "NO (ENCRYPT-NEEDED)");

        await connection.WriteLineAsync("LISTSCRIPTS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("NO ", StringComparison.Ordinal));

        await connection.WriteLineAsync("NOOP \"sync-tag\"");
        StringAssert.Contains(await connection.ReadLineAsync(), "OK (TAG \"sync-tag\")");

        const string literalTag = "line one\r\nline two";
        await connection.WriteLiteralCommandAsync("NOOP", literalTag, nonSynchronizing: true);
        var tagMarker = await connection.ReadLineAsync();
        Assert.AreEqual($"OK (TAG {{{Encoding.UTF8.GetByteCount(literalTag)}}}", tagMarker);
        Assert.AreEqual(literalTag, Encoding.UTF8.GetString(await connection.ReadBytesAsync(
            Encoding.UTF8.GetByteCount(literalTag))));
        Assert.AreEqual(") \"NOOP completed\"", await connection.ReadLineAsync());
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task TlsAuthenticatedSessionSupportsCompleteScriptLifecycle()
    {
        var port = ReservePort();
        await using var server = await ServerFixture.StartAsync(CreateEnvironment(port), port);
        await using var connection = await ConnectAuthenticatedAsync(port);

        await connection.WriteLineAsync("CAPABILITY");
        var authenticatedCapabilities = await connection.ReadCapabilityResponseAsync();
        CollectionAssert.Contains(authenticatedCapabilities, $"\"OWNER\" \"{TestUsername}\"");
        CollectionAssert.DoesNotContain(authenticatedCapabilities, "\"STARTTLS\"");

        await connection.WriteLineAsync("HAVESPACE \"primary\" 128");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        var invalidScript = "fileinto \"Archive\";";
        await connection.WriteLiteralCommandAsync("CHECKSCRIPT", invalidScript, nonSynchronizing: true);
        var invalidResponse = await connection.ReadLineAsync();
        Assert.IsTrue(invalidResponse.StartsWith("NO ", StringComparison.Ordinal));
        StringAssert.Contains(invalidResponse, "require");

        var script = "require [\"fileinto\"];\r\nfileinto \"Archive\";\r\n";
        await connection.WriteLiteralCommandAsync("PUTSCRIPT \"primary\"", script, nonSynchronizing: true);
        var putResponse = await connection.ReadLineAsync();
        Assert.IsTrue(putResponse.StartsWith("OK ", StringComparison.Ordinal), putResponse);

        await connection.WriteLineAsync("SETACTIVE \"primary\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("LISTSCRIPTS");
        Assert.AreEqual("\"primary\" ACTIVE", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("GETSCRIPT \"primary\"");
        Assert.AreEqual(script, await connection.ReadLiteralResponseAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("RENAMESCRIPT \"primary\" \"renamed\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("DELETESCRIPT \"renamed\"");
        StringAssert.Contains(await connection.ReadLineAsync(), "NO (ACTIVE)");

        await connection.WriteLineAsync("SETACTIVE \"\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DELETESCRIPT \"renamed\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("UNAUTHENTICATE");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("LISTSCRIPTS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("NO ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task SynchronizingLiteralsAndScriptCountQuotaAreEnforced()
    {
        var port = ReservePort();
        await using var server = await ServerFixture.StartAsync(
            CreateEnvironment(port, maximumScripts: 1),
            port);
        await using var connection = await ConnectAuthenticatedAsync(port);

        await connection.WriteLiteralHeaderAsync("PUTSCRIPT \"first\"", "keep;", nonSynchronizing: false);
        StringAssert.Contains(await connection.ReadLineAsync(), "OK \"Ready for literal data\"");
        await connection.WriteLiteralBodyAsync("keep;");
        var putResponse = await connection.ReadLineAsync();
        Assert.IsTrue(putResponse.StartsWith("OK ", StringComparison.Ordinal), putResponse);

        await connection.WriteLineAsync("HAVESPACE \"second\" 5");
        StringAssert.Contains(await connection.ReadLineAsync(), "NO (QUOTA/MAXSCRIPTS)");

        await connection.WriteLineAsync("HAVESPACE \"first\" 5");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLiteralCommandAsync("PUTSCRIPT \"second\"", "keep;", nonSynchronizing: true);
        StringAssert.Contains(await connection.ReadLineAsync(), "NO (QUOTA/MAXSCRIPTS)");

        await connection.WriteDeclaredLiteralHeaderAsync(
            "CHECKSCRIPT",
            1024 * 1024 + 1,
            nonSynchronizing: false);
        StringAssert.Contains(await connection.ReadLineAsync(), "NO (QUOTA/MAXSIZE)");
        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task AuthenticationWithoutInitialResponseAndFailureLimitAreSupported()
    {
        var port = ReservePort();
        await using var server = await ServerFixture.StartAsync(CreateEnvironment(port), port);
        await using var connection = await ConnectTlsAsync(port);

        await connection.WriteLineAsync("AUTHENTICATE \"PLAIN\"");
        Assert.AreEqual("\"\"", await connection.ReadLineAsync());
        await connection.WriteLineAsync($"\"{PlainCredentials()}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("UNAUTHENTICATE");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await connection.WriteLineAsync("AUTHENTICATE \"PLAIN\" \"AGJhZABiYWQ=\"");
            var response = await connection.ReadLineAsync();
            Assert.IsTrue(
                response.StartsWith(attempt == 5 ? "BYE " : "NO ", StringComparison.Ordinal),
                response);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OAuthEnabledSessionAdvertisesAndAcceptsXOAuth2()
    {
        var port = ReservePort();
        await using var server = await ServerFixture.StartAsync(
            CreateEnvironment(port, enableOAuth: true),
            port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);
        await connection.ReadCapabilityResponseAsync();
        await connection.WriteLineAsync("STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        var capabilities = await connection.ReadCapabilityResponseAsync();
        CollectionAssert.Contains(capabilities, "\"SASL\" \"PLAIN XOAUTH2\"");

        await connection.WriteLineAsync(
            $"AUTHENTICATE \"XOAUTH2\" \"{XOAuth2Credentials()}\"");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
    }

    private async Task<ProtocolConnection> ConnectAuthenticatedAsync(int port)
    {
        var connection = await ConnectTlsAsync(port);
        await connection.WriteLineAsync($"AUTHENTICATE \"PLAIN\" \"{PlainCredentials()}\"");
        var response = await connection.ReadLineAsync();
        if (!response.StartsWith("OK ", StringComparison.Ordinal))
        {
            await connection.DisposeAsync();
            Assert.Fail($"ManageSieve authentication failed: {response}");
        }
        return connection;
    }

    private async Task<ProtocolConnection> ConnectTlsAsync(int port)
    {
        var connection = await ProtocolConnection.ConnectAsync(port);
        await connection.ReadCapabilityResponseAsync();
        await connection.WriteLineAsync("STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        var capabilities = await connection.ReadCapabilityResponseAsync();
        CollectionAssert.Contains(capabilities, "\"SASL\" \"PLAIN\"");
        CollectionAssert.DoesNotContain(capabilities, "\"STARTTLS\"");
        return connection;
    }

    private EnvironmentConfig CreateEnvironment(
        int port,
        int maximumScripts = 64,
        bool enableOAuth = false) => new()
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
                EnableSmtp = false,
            },
            Imap = new ImapConfig
            {
                EnableImap = false,
            },
            Pop3 = new Pop3Config(),
            Sieve = new SieveConfig
            {
                Port = port,
                EnableManageSieve = true,
                EnableStartTls = true,
                MaxScriptsPerUser = maximumScripts,
            },
            Jmap = new JmapConfig
            {
                EnableJmap = false,
                IsDefault = false,
            },
            Dav = new DavConfig
            {
                EnableDav = false,
            },
            OAuth = new OAuthConfig
            {
                EnableOAuth = enableOAuth,
                PublicBaseUrl = "https://email.mk8n.com",
            },
            Tls = new TlsConfig
            {
                CertificatePath = _certificatePath,
            },
            Limits = new LimitsConfig
            {
                MaxMessageSizeBytes = 65_536,
                MaxRecipientsPerMessage = 100,
                ConnectionTimeoutSeconds = 10,
                MaxConnectionsPerIp = 10,
            },
        };

    private static string PlainCredentials() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));

    private static string XOAuth2Credentials() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"user={TestUsername}\u0001auth=Bearer {TestAccessToken}\u0001\u0001"));

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ServerFixture(
        ServiceProvider services,
        IHostedService hostedService) : IAsyncDisposable
    {
        public static async Task<ServerFixture> StartAsync(
            EnvironmentConfig environment,
            int port,
            IGatewayTrafficJournal? journal = null)
        {
            var serviceCollection = new ServiceCollection();
            var databaseName = $"manage-sieve-{Guid.NewGuid():N}";
            serviceCollection.AddSingleton<IMailAuthenticator>(new StubMailAuthenticator());
            serviceCollection.AddSingleton<IOAuthTokenService>(new StubOAuthTokenService());
            serviceCollection.AddScoped<ISieveScriptService, SieveScriptService>();
            serviceCollection.AddScoped<ISieveApplicationService, SieveApplicationService>();
            serviceCollection.AddDbContext<EmailDbContext>(options =>
                options.UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(warnings =>
                        warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            var services = serviceCollection.BuildServiceProvider();
            using (var scope = services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var company = new CompanyDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "Test Company",
                    IsActive = true,
                };
                database.Addresses.Add(new AddressDB
                {
                    Id = Guid.CreateVersion7(),
                    Domain = "mk8n.com",
                    IsActive = true,
                    Company = company,
                });
                database.Users.Add(new UserDB
                {
                    Id = TestUserId,
                    Username = TestUsername,
                    PasswordHash = PasswordHasher.Hash(TestPassword),
                    Role = "User",
                    IsActive = true,
                    Company = company,
                });
                database.SaveChanges();
            }

            var hostedService = new ManageSieveServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<ManageSieveServerService>.Instance,
                journal);
            var fixture = new ServerFixture(services, hostedService);
            await hostedService.StartAsync(CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    return fixture;
                }
                catch (SocketException)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }
            throw new TimeoutException($"The ManageSieve test server did not listen on port {port}.");
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await hostedService.StopAsync(timeout.Token);
            await services.DisposeAsync();
        }
    }

    private sealed class StubMailAuthenticator : IMailAuthenticator
    {
        public Task<AuthenticatedMailUser?> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            AuthenticatedMailUser? user =
                string.Equals(username, TestUsername, StringComparison.OrdinalIgnoreCase)
                && password == TestPassword
                    ? new AuthenticatedMailUser(TestUserId, TestUsername)
                    : null;
            return Task.FromResult(user);
        }
    }

    private sealed class StubOAuthTokenService : IOAuthTokenService
    {
        public Task<AuthenticatedMailUser?> AuthenticateAccessTokenAsync(
            string accessToken,
            string requiredScope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedMailUser?>(
                accessToken == TestAccessToken && requiredScope == "sieve"
                    ? new(TestUserId, TestUsername)
                    : null);

        public Task<OAuthTokenPair?> CreateGrantAsync(
            Guid userId,
            string clientId,
            string deviceName,
            IReadOnlyCollection<string> scopes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OAuthTokenPair?> RefreshAsync(
            string refreshToken,
            string clientId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<OAuthGrantSummary>> ListGrantsAsync(
            Guid userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RevokeGrantAsync(
            Guid userId,
            Guid grantId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeTokenAsync(
            string token,
            string clientId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProtocolConnection : IAsyncDisposable
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly TcpClient _client;
        private Stream _stream;

        private ProtocolConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<ProtocolConnection> ConnectAsync(int port)
        {
            var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return new ProtocolConnection(client);
        }

        public async Task<List<string>> ReadCapabilityResponseAsync()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync();
                lines.Add(line);
                if (line.StartsWith("OK", StringComparison.Ordinal)
                    || line.StartsWith("NO", StringComparison.Ordinal)
                    || line.StartsWith("BYE", StringComparison.Ordinal))
                {
                    return lines;
                }
            }
        }

        public async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new List<byte>();
            while (true)
            {
                var single = new byte[1];
                var read = await _stream.ReadAsync(single, timeout.Token);
                if (read == 0)
                    throw new EndOfStreamException("The server closed the ManageSieve stream.");
                if (single[0] == '\n')
                {
                    Assert.IsTrue(bytes.Count > 0 && bytes[^1] == '\r');
                    bytes.RemoveAt(bytes.Count - 1);
                    return StrictUtf8.GetString(bytes.ToArray());
                }
                bytes.Add(single[0]);
            }
        }

        public async Task WriteLineAsync(string line)
        {
            await WriteRawAsync(line + "\r\n");
        }

        public async Task WriteLiteralCommandAsync(
            string command,
            string literal,
            bool nonSynchronizing)
        {
            await WriteLiteralHeaderAsync(command, literal, nonSynchronizing);
            await WriteLiteralBodyAsync(literal);
        }

        public Task WriteLiteralHeaderAsync(
            string command,
            string literal,
            bool nonSynchronizing)
        {
            var byteCount = StrictUtf8.GetByteCount(literal);
            return WriteDeclaredLiteralHeaderAsync(command, byteCount, nonSynchronizing);
        }

        public Task WriteDeclaredLiteralHeaderAsync(
            string command,
            int byteCount,
            bool nonSynchronizing) =>
            WriteRawAsync($"{command} {{{byteCount}{(nonSynchronizing ? "+" : string.Empty)}}}\r\n");

        public Task WriteLiteralBodyAsync(string literal) => WriteRawAsync(literal + "\r\n");

        public async Task<string> ReadLiteralResponseAsync()
        {
            var marker = await ReadLineAsync();
            Assert.IsTrue(marker.Length >= 3 && marker[0] == '{' && marker[^1] == '}');
            Assert.IsTrue(int.TryParse(marker.AsSpan(1, marker.Length - 2), out var size));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new byte[size];
            await _stream.ReadExactlyAsync(bytes, timeout.Token);
            Assert.AreEqual(string.Empty, await ReadLineAsync());
            return StrictUtf8.GetString(bytes);
        }

        public async Task<byte[]> ReadBytesAsync(int size)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new byte[size];
            await _stream.ReadExactlyAsync(bytes, timeout.Token);
            return bytes;
        }

        public async Task UpgradeToTlsAsync(string hostName)
        {
            var tlsStream = new SslStream(
                _stream,
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostName },
                timeout.Token);
            _stream = tlsStream;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _client.Dispose();
        }

        private async Task WriteRawAsync(string value)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _stream.WriteAsync(StrictUtf8.GetBytes(value), timeout.Token);
            await _stream.FlushAsync(timeout.Token);
        }
    }

    private sealed class RecordingSieveJournal : IGatewayTrafficJournal
    {
        public ConcurrentQueue<GatewayTrafficRecord> Records { get; } = new();
        public bool RejectWrites { get; init; }

        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default)
        {
            if (RejectWrites)
                throw new InvalidOperationException("The journal is unavailable.");
            Records.Enqueue(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GatewayTrafficRecord>>(
                Records.Where(record => record.SessionId == sessionId).ToArray());
    }
}
