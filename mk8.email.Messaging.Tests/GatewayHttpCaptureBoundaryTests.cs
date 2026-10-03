using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Gateway.Security;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals creates this integration class; discovery is verified by executed test counts.")]
internal sealed class GatewayHttpCaptureBoundaryTests
{
    [TestMethod]
    public async Task ProductionKnownLengthRejectionRecordsBothDirectionsWithoutWorkerDispatch()
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var content = new ByteArrayContent(new byte[65_537]);
        using var response = await fixture.Client.PostAsync(new Uri("/oauth/token", UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        StringAssert.Contains(body, "invalid_request", StringComparison.Ordinal);
        await fixture.AssertRecordedResponseAsync("oauth", "/oauth/token", 413, body, rejection: true).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.EndpointCalls);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("/jmap/api", "jmap", "application/problem+json")]
    [DataRow("/oauth/token", "oauth", "application/json")]
    public async Task ProductionUnexpectedFailureKeepsOriginalProtocolAndSession(string path, string protocol, string mediaType)
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Test-Failure", "true");
        using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(mediaType, response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        Assert.IsFalse(body.Contains("deliberate secret exception", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync(protocol, path, 500, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ProductionChunkedKestrelRejectsBeforeRemainingBodyOrTerminatorArrive()
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var connection = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await connection.ConnectAsync(fixture.Address.Host, fixture.Address.Port, deadline.Token).ConfigureAwait(false);
        var stream = connection.GetStream();
        var headers = Encoding.ASCII.GetBytes("POST /oauth/token HTTP/1.1\r\nHost: localhost\r\n"
            + "X-Forwarded-Proto: https\r\nContent-Type: application/x-www-form-urlencoded\r\n"
            + "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n10001\r\n");
        await stream.WriteAsync(headers, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[65_537], deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        // Deliberately omit the rest of this chunk, its CRLF and the final zero chunk.
        // A whole-body buffer would wait here; the per-request Kestrel budget must reject now.
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var status = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        StringAssert.Contains(status!, "413", StringComparison.Ordinal);
        var contentLength = 0;
        while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { Length: > 0 } line)
            if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.AsSpan(16), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(0, contentLength);
        var bodyCharacters = new char[contentLength];
        Assert.AreEqual(contentLength, await reader.ReadBlockAsync(bodyCharacters.AsMemory(), deadline.Token).ConfigureAwait(false));
        var body = new string(bodyCharacters);
        StringAssert.Contains(body, "invalid_request", StringComparison.Ordinal);
        await fixture.AssertRecordedResponseAsync("oauth", "/oauth/token", 413, body, rejection: true).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.EndpointCalls);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AdvertisedBoundaryFitsRealJournalBlobTransportAndDownloadWhileNextByteIsRejected()
    {
        var fixture = await CaptureFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using (var session = await fixture.Client.GetAsync(new Uri("/jmap/session", UriKind.Relative)).ConfigureAwait(false))
        {
            session.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await session.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.AreEqual(CaptureFixture.UploadBytes, document.RootElement.GetProperty("capabilities")
                .GetProperty("urn:ietf:params:jmap:core").GetProperty("maxSizeUpload").GetInt32());
        }
        var contentBytes = new byte[CaptureFixture.UploadBytes];
        Array.Fill(contentBytes, (byte)251); // Base64 '+' must not acquire JSON '\u002B' expansion.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/jmap/upload/account"))
        {
            request.Content = new ByteArrayContent(contentBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Add("X-Capture-Metadata", new string('<', 26_000));
            using var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }
        using (var response = await fixture.Client.GetAsync(new Uri("/jmap/download/account/blob/file", UriKind.Relative)).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            CollectionAssert.AreEqual(contentBytes, await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }
        using (var content = new ByteArrayContent(new byte[CaptureFixture.UploadBytes + 1]))
        using (var response = await fixture.Client.PostAsync(new Uri("/jmap/upload/account", UriKind.Relative), content).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            await fixture.AssertRecordedResponseAsync("jmap", "/jmap/upload/account", 413,
                await response.Content.ReadAsStringAsync().ConfigureAwait(false), rejection: true).ConfigureAwait(false);
        }
        Assert.AreEqual(1, fixture.Application.UploadCalls);
        CollectionAssert.AreEqual(contentBytes, fixture.Application.Content!);
        await fixture.AssertCapturedUploadBytesAsync(contentBytes).ConfigureAwait(false);
        await fixture.AssertPayloadBudgetsAndBlobReferencesAsync().ConfigureAwait(false);
    }

    private sealed class CaptureFixture : IAsyncDisposable
    {
        internal const int UploadBytes = 2 * 1024 * 1024;
        private readonly PostgresTestDatabase _database;
        private readonly NpgsqlDataSource _dataSource;
        private readonly AesGcmPayloadProtector _protector;
        private readonly BlobContainerClient _container;
        private readonly ServiceProvider _workerProvider;
        private readonly ApplicationRequestWorker _worker;
        private readonly WebApplication _host;
        private readonly PostgresGatewayTrafficJournal _journal;
        private readonly int _maximumPayloadBytes;

        private CaptureFixture(PostgresTestDatabase database, NpgsqlDataSource dataSource, AesGcmPayloadProtector protector,
            BlobContainerClient container, ServiceProvider workerProvider, ApplicationRequestWorker worker,
            WebApplication host, PostgresGatewayTrafficJournal journal, UploadApplication application, int maximumPayloadBytes)
        {
            _database = database;
            _dataSource = dataSource;
            _protector = protector;
            _container = container;
            _workerProvider = workerProvider;
            _worker = worker;
            _host = host;
            _journal = journal;
            Application = application;
            _maximumPayloadBytes = maximumPayloadBytes;
            Address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(15) };
            Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0LXByb3RvY29sLXNlY3JldA==");
        }

        public Uri Address { get; }
        public HttpClient Client { get; }
        public UploadApplication Application { get; }
        public int EndpointCalls { get; private set; }
        private int ErrorPageCalls { get; set; }

        public static async Task<CaptureFixture> CreateAsync()
        {
            var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
            var blobConnection = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
            if (database is null || string.IsNullOrWhiteSpace(blobConnection))
                throw new AssertInconclusiveException("PostgreSQL and an Azure Blob-compatible test endpoint are required.");
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
            var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "http-boundary-key");
            var container = new BlobServiceClient(blobConnection).GetBlobContainerClient($"mk8-capture-{Guid.NewGuid():N}");
            var store = new AzureBlobLargeObjectStore(new BlobServiceClient(blobConnection),
                new AzureBlobLargeObjectStoreOptions { ContainerName = container.Name, CreateContainerIfMissing = true });
            var maximumPayloadBytes = checked((int)GatewayHttpPayloadBudget.BinaryEnvelopeBytes(UploadBytes));
            var options = new PostgresMessagingOptions
            {
                MaxPayloadBytes = maximumPayloadBytes, InlinePayloadThresholdBytes = 1024,
                NotificationFallbackInterval = TimeSpan.FromMilliseconds(100),
            };
            var bus = new PostgresApplicationBus(dataSource, protector, options, largeObjectStore: store);
            var journal = new PostgresGatewayTrafficJournal(dataSource, protector, options, store);
            var application = new UploadApplication();
            var provider = new ServiceCollection().AddSingleton<IJmapApplicationService>(application)
                .AddScoped<IApplicationRequestDispatcher>(services => new ApplicationRequestDispatcher(services)).BuildServiceProvider();
            var worker = new ApplicationRequestWorker(bus, provider.GetRequiredService<IServiceScopeFactory>(),
                new ApplicationWorkerIdentity("worker@http-boundary", TimeSpan.FromSeconds(30)), NullLogger<ApplicationRequestWorker>.Instance);
            var environment = CreateEnvironment(maximumPayloadBytes, blobConnection);
            Assert.HasCount(0, environment.Validate(role: EnvironmentValidationRole.Gateway));
            Assert.HasCount(0, environment.Validate(role: EnvironmentValidationRole.ApplicationWorker));
            var host = BuildProductionHost(environment, bus, journal);
            host.Use((context, next) => context.Request.Headers.ContainsKey("X-Test-Failure")
                ? throw new InvalidOperationException("deliberate secret exception") : next(context));
            CaptureFixture? fixture = null;
            host.MapPost("/oauth/token", () => { fixture!.EndpointCalls++; return Results.Json(new { ok = true }); });
            host.MapGet("/Error", () => { fixture!.ErrorPageCalls++; return Results.Content("ADMIN ERROR HTML", "text/html"); });
            host.MapJmapEndpoints();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
            fixture = new CaptureFixture(database, dataSource, protector, container, provider, worker, host, journal, application, maximumPayloadBytes);
            return fixture;
        }

        private static WebApplication BuildProductionHost(EnvironmentConfig environment,
            IApplicationRequestClient bus, IGatewayTrafficJournal journal)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Listen(IPAddress.Loopback, 0);
                server.Limits.MaxRequestBodySize = UploadBytes;
                server.Limits.MaxRequestHeadersTotalSize = GatewayHttpPayloadBudget.RequestHeadersBytes;
                server.Limits.MaxRequestLineSize = GatewayHttpPayloadBudget.RequestLineBytes;
            });
            builder.Services.AddSingleton(environment).AddSingleton(environment.Admin).AddSingleton<AdminNetworkPolicy>();
            builder.Services.AddSingleton<IApplicationRequestClient>(bus).AddSingleton<IGatewayTrafficJournal>(journal);
            builder.Services.AddGatewayApplicationClient();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(_ => { });
            builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
                forwarded.KnownProxies.Add(IPAddress.Loopback);
            });
            var host = builder.Build();
            GatewayHttpPipeline.Configure(host); // The identical Production pipeline used by Program.cs.
            return host;
        }

        private static EnvironmentConfig CreateEnvironment(int maximumPayloadBytes, string blobConnection) => new()
        {
            Database = new DatabaseConfig { Host = "database.test", Password = "test-only-database-secret" },
            Smtp = new SmtpConfig { Hostname = "email.example.test", EnableSmtp = false },
            Imap = new ImapConfig { EnableImap = false, EnableImplicitTls = false },
            Dav = new DavConfig { EnableDav = false },
            Jmap = new JmapConfig { MaxUploadSizeBytes = UploadBytes, MaxRequestSizeBytes = 65_536 },
            Limits = new LimitsConfig { MaxMessageSizeBytes = 1_048_576 },
            Admin = new AdminConfig { AllowedNetworks = ["127.0.0.0/8"], DataProtectionKeyPath = "/tmp/mk8-capture-keys",
                AuditLogPath = "/tmp/mk8-capture-audit", HealthStatusPath = "/tmp/mk8-capture-health" },
            Messaging = new MessagingConfig { Enabled = true, MaxPayloadBytes = maximumPayloadBytes,
                EncryptionKey = Convert.ToBase64String(new byte[32]), InlinePayloadThresholdBytes = 1024 },
            ObjectStorage = new ObjectStorageConfig { ConnectionString = blobConnection },
        };

        public async Task AssertRecordedResponseAsync(string protocol, string path, int status, string body, bool rejection)
        {
            var command = _dataSource.CreateCommand("SELECT session_id FROM gateway_traffic_records WHERE sequence = 0 AND metadata->>'layer' = 'presentation' ORDER BY recorded_at DESC LIMIT 1");
            await using var commandLifetime = command.ConfigureAwait(false);
            var sessionId = (Guid)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
            var records = await _journal.ReadSessionAsync(sessionId).ConfigureAwait(false);
            Assert.HasCount(2, records);
            Assert.IsTrue(records.All(record => string.Equals(record.Protocol, protocol, StringComparison.Ordinal)));
            Assert.AreEqual(GatewayTrafficDirections.Inbound, records[0].Direction, StringComparer.Ordinal);
            Assert.AreEqual(GatewayTrafficDirections.Outbound, records[1].Direction, StringComparer.Ordinal);
            using var inbound = JsonDocument.Parse(records[0].Payload);
            Assert.AreEqual(path, inbound.RootElement.GetProperty("path").GetString(), StringComparer.Ordinal);
            Assert.AreEqual(rejection, inbound.RootElement.GetProperty("rejection").ValueKind != JsonValueKind.Null);
            using var outbound = JsonDocument.Parse(records[1].Payload);
            Assert.AreEqual(status, outbound.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual(body, Encoding.UTF8.GetString(Convert.FromBase64String(outbound.RootElement.GetProperty("bodyBase64").GetString()!)), StringComparer.Ordinal);
            Assert.AreEqual(0, ErrorPageCalls);
            Assert.IsFalse(body.Contains("ADMIN ERROR HTML", StringComparison.Ordinal));
        }

        public async Task AssertNoWorkerRequestsAsync()
        {
            var command = _dataSource.CreateCommand("SELECT count(*) FROM application_requests");
            await using var commandLifetime = command.ConfigureAwait(false);
            Assert.AreEqual(0L, await command.ExecuteScalarAsync().ConfigureAwait(false));
        }

        public async Task AssertPayloadBudgetsAndBlobReferencesAsync()
        {
            var command = _dataSource.CreateCommand("""
                SELECT (SELECT max(payload_length) FROM gateway_traffic_records),
                       (SELECT max(request_payload_length) FROM application_requests),
                       (SELECT max(response_payload_length) FROM application_requests),
                       (SELECT count(*) FROM gateway_traffic_records WHERE payload_blob_provider = 'azure-blob'),
                       (SELECT count(*) FROM application_requests WHERE request_payload_blob_provider = 'azure-blob')
                """);
            await using var commandLifetime = command.ConfigureAwait(false);
            var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            for (var index = 0; index < 3; index++)
                Assert.IsLessThanOrEqualTo(_maximumPayloadBytes, reader.GetInt64(index));
            Assert.IsGreaterThanOrEqualTo(2L, reader.GetInt64(3));
            Assert.IsGreaterThanOrEqualTo(1L, reader.GetInt64(4));
        }

        public async Task AssertCapturedUploadBytesAsync(byte[] expected)
        {
            var command = _dataSource.CreateCommand("SELECT session_id FROM gateway_traffic_records WHERE sequence = 0 AND metadata->>'layer' = 'presentation' ORDER BY recorded_at");
            await using var commandLifetime = command.ConfigureAwait(false);
            var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var records = await _journal.ReadSessionAsync(reader.GetGuid(0)).ConfigureAwait(false);
                using var inbound = JsonDocument.Parse(records[0].Payload);
                if (string.Equals(inbound.RootElement.GetProperty("path").GetString(), "/jmap/upload/account", StringComparison.Ordinal)
                    && inbound.RootElement.GetProperty("rejection").ValueKind == JsonValueKind.Null)
                {
                    CollectionAssert.AreEqual(expected, Convert.FromBase64String(inbound.RootElement.GetProperty("bodyBase64").GetString()!));
                    return;
                }
            }
            Assert.Fail("The successful upload has no durable external inbound record.");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync().ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false);
            await _worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _worker.Dispose();
            await _workerProvider.DisposeAsync().ConfigureAwait(false);
            await _container.DeleteIfExistsAsync().ConfigureAwait(false);
            _protector.Dispose();
            await _dataSource.DisposeAsync().ConfigureAwait(false);
            await _database.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class UploadApplication : IJmapApplicationService
    {
        public int UploadCalls { get; private set; }
        public byte[]? Content { get; private set; }
        public Task<JmapApplicationResult> GetProfileAsync(JmapProfileApplicationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok, Profile: new JmapApplicationProfile("test",
                new JmapServiceLimits(CaptureFixture.UploadBytes, 1, 65_536, 1, 64, 500, 500, 32, 255, 1_048_576, [], []), [])));
        public Task<JmapApplicationResult> UploadAsync(JmapUploadApplicationRequest request, CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            Content = request.Content;
            return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok, BlobId: "blob", Size: Content.Length, ContentType: request.ContentType));
        }
        public Task<JmapApplicationResult> DownloadAsync(JmapDownloadApplicationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok, Content: Content, ContentType: "application/octet-stream"));
        public Task<JmapApplicationResult> ValidatePlanAsync(MailPlanApplicationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JmapApplicationResult> ExecuteOperationAsync(MailOperationApplicationRequest request, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JmapApplicationResult> PollChangesAsync(JmapChangesApplicationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
