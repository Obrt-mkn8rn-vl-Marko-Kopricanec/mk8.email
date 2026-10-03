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
using mk8.email.Contracts.Storage;
using mk8.email.Gateway.Protocols.Sieve;
using mk8.email.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ManageSieveProtocolTests
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
        {
            var server = (await ServerFixture.StartAsync(
                         CreateEnvironment(port), port, journal).ConfigureAwait(false));
            await using var serverLifetime = server.ConfigureAwait(false);
            var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
            await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
            Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
        }

        var sessionId = journal.Records.Single(record => string.Equals(record.Direction, GatewayTrafficDirections.Inbound
, StringComparison.Ordinal) && Encoding.UTF8.GetString(record.Payload).Contains("NOOP\r\n", StringComparison.Ordinal))
            .SessionId;
        var records = journal.Records.Where(record => record.SessionId == sessionId).ToArray();
        Assert.IsTrue(records.Any(record => string.Equals(record.Direction, GatewayTrafficDirections.Outbound
, StringComparison.Ordinal) && Encoding.UTF8.GetString(record.Payload).Contains("\"IMPLEMENTATION\"", StringComparison.Ordinal)));
        Assert.IsTrue(records.All(record => string.Equals(record.Protocol, "sieve", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, records.Length).Select(value => (long)value).ToArray(),
            records.Select(record => record.Sequence).ToArray());

        var rejectedPort = ReservePort();
        var rejectedServer = (await ServerFixture.StartAsync(
            CreateEnvironment(rejectedPort),
            rejectedPort,
            new RecordingSieveJournal { RejectWrites = true }).ConfigureAwait(false));
        await using var rejectedServerLifetime = rejectedServer.ConfigureAwait(false);
        var rejectedConnection = (await ProtocolConnection.ConnectAsync(rejectedPort).ConfigureAwait(false));
        await using var rejectedConnectionLifetime = rejectedConnection.ConfigureAwait(false);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => rejectedConnection.ReadLineAsync()).ConfigureAwait(false);
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
        var server = (await ServerFixture.StartAsync(CreateEnvironment(port), port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        var capabilities = await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
        CollectionAssert.Contains(capabilities, "\"VERSION\" \"1.0\"");
        CollectionAssert.Contains(capabilities, "\"SASL\" \"\"");
        CollectionAssert.Contains(capabilities, "\"STARTTLS\"");
        Assert.IsTrue(capabilities.Any(line => line.StartsWith("\"SIEVE\" ", StringComparison.Ordinal)));
        Assert.IsTrue(capabilities[^1].StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync($"AUTHENTICATE \"PLAIN\" \"{PlainCredentials()}\"").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "NO (ENCRYPT-NEEDED)", StringComparison.Ordinal);

        await connection.WriteLineAsync("LISTSCRIPTS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("NO ", StringComparison.Ordinal));

        await connection.WriteLineAsync("NOOP \"sync-tag\"").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "OK (TAG \"sync-tag\")", StringComparison.Ordinal);

        const string literalTag = "line one\r\nline two";
        await connection.WriteLiteralCommandAsync("NOOP", literalTag, nonSynchronizing: true).ConfigureAwait(false);
        var tagMarker = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.AreEqual($"OK (TAG {{{Encoding.UTF8.GetByteCount(literalTag)}}}", tagMarker, StringComparer.Ordinal);
        Assert.AreEqual(literalTag, Encoding.UTF8.GetString(await connection.ReadBytesAsync(
            Encoding.UTF8.GetByteCount(literalTag)).ConfigureAwait(false)), StringComparer.Ordinal);
        Assert.AreEqual(") \"NOOP completed\"", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task TlsAuthenticatedSessionSupportsCompleteScriptLifecycle()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartAsync(CreateEnvironment(port), port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ConnectAuthenticatedAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.WriteLineAsync("CAPABILITY").ConfigureAwait(false);
        var authenticatedCapabilities = await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
        CollectionAssert.Contains(authenticatedCapabilities, $"\"OWNER\" \"{TestUsername}\"");
        CollectionAssert.DoesNotContain(authenticatedCapabilities, "\"STARTTLS\"");

        await connection.WriteLineAsync("HAVESPACE \"primary\" 128").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        var invalidScript = "fileinto \"Archive\";";
        await connection.WriteLiteralCommandAsync("CHECKSCRIPT", invalidScript, nonSynchronizing: true).ConfigureAwait(false);
        var invalidResponse = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(invalidResponse.StartsWith("NO ", StringComparison.Ordinal));
        StringAssert.Contains(invalidResponse, "require", StringComparison.Ordinal);

        var script = "require [\"fileinto\"];\r\nfileinto \"Archive\";\r\n";
        await connection.WriteLiteralCommandAsync("PUTSCRIPT \"primary\"", script, nonSynchronizing: true).ConfigureAwait(false);
        var putResponse = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(putResponse.StartsWith("OK ", StringComparison.Ordinal), putResponse);

        await connection.WriteLineAsync("SETACTIVE \"primary\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("LISTSCRIPTS").ConfigureAwait(false);
        Assert.AreEqual("\"primary\" ACTIVE", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("GETSCRIPT \"primary\"").ConfigureAwait(false);
        Assert.AreEqual(script, await connection.ReadLiteralResponseAsync().ConfigureAwait(false), StringComparer.Ordinal);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("RENAMESCRIPT \"primary\" \"renamed\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("DELETESCRIPT \"renamed\"").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "NO (ACTIVE)", StringComparison.Ordinal);

        await connection.WriteLineAsync("SETACTIVE \"\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DELETESCRIPT \"renamed\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("UNAUTHENTICATE").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
        await connection.WriteLineAsync("LISTSCRIPTS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("NO ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task SynchronizingLiteralsAndScriptCountQuotaAreEnforced()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartAsync(
            CreateEnvironment(port, maximumScripts: 1),
            port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ConnectAuthenticatedAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.WriteLiteralHeaderAsync("PUTSCRIPT \"first\"", "keep;", nonSynchronizing: false).ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "OK \"Ready for literal data\"", StringComparison.Ordinal);
        await connection.WriteLiteralBodyAsync("keep;").ConfigureAwait(false);
        var putResponse = await connection.ReadLineAsync().ConfigureAwait(false);
        Assert.IsTrue(putResponse.StartsWith("OK ", StringComparison.Ordinal), putResponse);

        await connection.WriteLineAsync("HAVESPACE \"second\" 5").ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "NO (QUOTA/MAXSCRIPTS)", StringComparison.Ordinal);

        await connection.WriteLineAsync("HAVESPACE \"first\" 5").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLiteralCommandAsync("PUTSCRIPT \"second\"", "keep;", nonSynchronizing: true).ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "NO (QUOTA/MAXSCRIPTS)", StringComparison.Ordinal);

        await connection.WriteDeclaredLiteralHeaderAsync(
            "CHECKSCRIPT",
            1024 * 1024 + 1,
            nonSynchronizing: false).ConfigureAwait(false);
        StringAssert.Contains(await connection.ReadLineAsync().ConfigureAwait(false), "NO (QUOTA/MAXSIZE)", StringComparison.Ordinal);
        await connection.WriteLineAsync("NOOP").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task AuthenticationWithoutInitialResponseAndFailureLimitAreSupported()
    {
        var port = ReservePort();
        var server = (await ServerFixture.StartAsync(CreateEnvironment(port), port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ConnectTlsAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);

        await connection.WriteLineAsync("AUTHENTICATE \"PLAIN\"").ConfigureAwait(false);
        Assert.AreEqual("\"\"", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await connection.WriteLineAsync($"\"{PlainCredentials()}\"").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));

        await connection.WriteLineAsync("UNAUTHENTICATE").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await connection.WriteLineAsync("AUTHENTICATE \"PLAIN\" \"AGJhZABiYWQ=\"").ConfigureAwait(false);
            var response = await connection.ReadLineAsync().ConfigureAwait(false);
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
        var server = (await ServerFixture.StartAsync(
            CreateEnvironment(port, enableOAuth: true),
            port).ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = (await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false));
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com").ConfigureAwait(false);
        var capabilities = await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
        CollectionAssert.Contains(capabilities, "\"SASL\" \"PLAIN XOAUTH2\"");

        await connection.WriteLineAsync(
            $"AUTHENTICATE \"XOAUTH2\" \"{XOAuth2Credentials()}\"").ConfigureAwait(false);

        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
    }

    private static async Task<ProtocolConnection> ConnectAuthenticatedAsync(int port)
    {
        var connection = await ConnectTlsAsync(port).ConfigureAwait(false);
        await connection.WriteLineAsync($"AUTHENTICATE \"PLAIN\" \"{PlainCredentials()}\"").ConfigureAwait(false);
        var response = await connection.ReadLineAsync().ConfigureAwait(false);
        if (!response.StartsWith("OK ", StringComparison.Ordinal))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            Assert.Fail($"ManageSieve authentication failed: {response}");
        }
        return connection;
    }

    private static async Task<ProtocolConnection> ConnectTlsAsync(int port)
    {
        var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
        await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
        await connection.WriteLineAsync("STARTTLS").ConfigureAwait(false);
        Assert.IsTrue((await connection.ReadLineAsync().ConfigureAwait(false)).StartsWith("OK ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com").ConfigureAwait(false);
        var capabilities = await connection.ReadCapabilityResponseAsync().ConfigureAwait(false);
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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ServerFixture(
        ServiceProvider services,
        IHostedService hostedService) : IAsyncDisposable
    {

        public static async Task<ServerFixture> StartAsync(EnvironmentConfig environment, int port, IGatewayTrafficJournal? journal = null)
        {

            // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
            ServiceProvider? services = CreateServices();

#pragma warning restore CA2000
            try
            {
                await SeedAsync(services).ConfigureAwait(false);
                var fixture = await StartOwnedAsync(services, environment, port, journal).ConfigureAwait(false);
                services = null;
                return fixture;
            }
            finally
            {

                // Successful transfer clears the resource; initialization exceptions leave it non-null for finally cleanup.
#pragma warning disable CA1508
                if (services is not null) await services.DisposeAsync().ConfigureAwait(false);

#pragma warning restore CA1508
            }
        }

        private static ServiceProvider CreateServices()
        {
            var serviceCollection = new ServiceCollection();
            var databaseName = $"manage-sieve-{Guid.NewGuid():N}";
            serviceCollection.AddSingleton<IMailAuthenticator>(new StubMailAuthenticator());
            serviceCollection.AddSingleton<IOAuthTokenService>(new StubOAuthTokenService());
            serviceCollection.AddScoped<ISieveScriptService, SieveScriptService>();
            serviceCollection.AddScoped<ISieveApplicationService, SieveApplicationService>();
            serviceCollection.AddSingleton<InMemoryLargeObjectStore>();
            serviceCollection.AddSingleton<ILargeObjectStore>(provider =>
                provider.GetRequiredService<InMemoryLargeObjectStore>());
            serviceCollection.AddScoped<LargeObjectTransactionEffects>();
            serviceCollection.AddScoped<SieveScriptContentService>();
            serviceCollection.AddLogging();
            serviceCollection.AddDbContext<EmailDbContext>(options =>
                options.UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(warnings =>
                        warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            return serviceCollection.BuildServiceProvider();

        }

        private static async Task SeedAsync(ServiceProvider services)
        {
            using (var scope = services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var company = new CompanyDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "Test Company",
                    IsActive = true,
                };
                await database.Addresses.AddAsync(new AddressDB
                {
                    Id = Guid.CreateVersion7(),
                    Domain = "mk8n.com",
                    IsActive = true,
                    Company = company,
                }).ConfigureAwait(false);
                await database.Users.AddAsync(new UserDB
                {
                    Id = TestUserId,
                    Username = TestUsername,
                    PasswordHash = PasswordHasher.Hash(TestPassword),
                    Role = "User",
                    IsActive = true,
                    Company = company,
                }).ConfigureAwait(false);
                await database.SaveChangesAsync().ConfigureAwait(false);
            }


        }

        private static async Task<ServerFixture> StartOwnedAsync(ServiceProvider services, EnvironmentConfig environment, int port, IGatewayTrafficJournal? journal)
        {

            // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
            ManageSieveServerService? hostedService = new ManageSieveServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<ManageSieveServerService>.Instance,
                journal);

#pragma warning restore CA2000

            try
            {
                await StartAndWaitAsync(hostedService, port).ConfigureAwait(false);
                var fixture = new ServerFixture(services, hostedService);
                hostedService = null;
                return fixture;
            }
            finally
            {

                // Successful transfer clears the resource; initialization exceptions leave it non-null for finally cleanup.
#pragma warning disable CA1508
                if (hostedService is not null)
                {
                    try { await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally { hostedService.Dispose(); }
                }

#pragma warning restore CA1508
            }
        }

        private static async Task StartAndWaitAsync(ManageSieveServerService hostedService, int port)
        {
            await hostedService.StartAsync(CancellationToken.None).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
            }
            throw new TimeoutException($"The ManageSieve test server did not listen on port {port}.");
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await hostedService.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                (hostedService as IDisposable)?.Dispose();
                await services.DisposeAsync().ConfigureAwait(false);
            }
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
                && string.Equals(password, TestPassword
, StringComparison.Ordinal) ? new AuthenticatedMailUser(TestUserId, TestUsername)
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
string.Equals(accessToken, TestAccessToken, StringComparison.Ordinal) && string.Equals(requiredScope, "sieve"
, StringComparison.Ordinal) ? new(TestUserId, TestUsername)
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
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return new ProtocolConnection(client);
        }

        public async Task<List<string>> ReadCapabilityResponseAsync()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync().ConfigureAwait(false);
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
                var read = await _stream.ReadAsync(single, timeout.Token).ConfigureAwait(false);
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
            await WriteRawAsync(line + "\r\n").ConfigureAwait(false);
        }

        public async Task WriteLiteralCommandAsync(
            string command,
            string literal,
            bool nonSynchronizing)
        {
            await WriteLiteralHeaderAsync(command, literal, nonSynchronizing).ConfigureAwait(false);
            await WriteLiteralBodyAsync(literal).ConfigureAwait(false);
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
            var marker = await ReadLineAsync().ConfigureAwait(false);
            Assert.IsTrue(marker.Length >= 3 && marker[0] == '{' && marker[^1] == '}');
            Assert.IsTrue(int.TryParse(marker.AsSpan(1, marker.Length - 2), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new byte[size];
            await _stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, await ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            return StrictUtf8.GetString(bytes);
        }

        public async Task<byte[]> ReadBytesAsync(int size)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var bytes = new byte[size];
            await _stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            return bytes;
        }

        public async Task UpgradeToTlsAsync(string hostName)
        {
            var tlsStream = new SslStream(
                _stream,
                leaveInnerStreamOpen: false,
                (_, certificate, _, errors) => TestCertificateFactory.IsTrusted(certificate, errors));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostName },
                timeout.Token).ConfigureAwait(false);
            _stream = tlsStream;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _client.Dispose();
        }

        private async Task WriteRawAsync(string value)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _stream.WriteAsync(StrictUtf8.GetBytes(value), timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
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
