using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class JmapGatewayRouteTests
{
    [TestMethod]
    public async Task HttpApiRequestCrossesRemoteWorkerAndRecordsBothBoundaries()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "jmap-route-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "jmap-route-key");
        var messagingOptions = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gatewayBus = new PostgresApplicationBus(
            gatewayDataSource,
            gatewayProtector,
            messagingOptions);
        var workerBus = new PostgresApplicationBus(
            workerDataSource,
            workerProtector,
            messagingOptions);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource,
            gatewayProtector,
            messagingOptions);
        var jmap = new StubJmapApplicationService();
        var workerServices = new ServiceCollection()
            .AddSingleton<IJmapApplicationService>(jmap)
            .AddScoped<IApplicationRequestDispatcher>(serviceProvider =>
                new ApplicationRequestDispatcher(serviceProvider));
        await using var workerProvider = workerServices.BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@jmap-route-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var environment = new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "email.example.test" },
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                PublicBaseUrl = "https://email.example.test",
                MaxRequestSizeBytes = 65_536,
                MaxUploadSizeBytes = 1_048_576,
            },
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton<IApplicationRequestClient>(gatewayBus);
        builder.Services.AddSingleton<IGatewayTrafficJournal>(journal);
        builder.Services.AddGatewayApplicationClient();
        var application = builder.Build();
        application.UseMiddleware<GatewayProtocolTrafficCaptureMiddleware>();
        application.UseMiddleware<GatewayApplicationFailureMiddleware>();
        application.UseRouting();
        application.MapJmapEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await worker.StartAsync(timeout.Token);
        await application.StartAsync(timeout.Token);
        try
        {
            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new AssertFailedException("The JMAP Gateway did not publish an address.");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10),
            };
            const string requestDocument =
                "{\"using\":[\"urn:ietf:params:jmap:core\"],\"methodCalls\":[]}";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/jmap/api")
            {
                Content = new StringContent(requestDocument, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("person@example.test:route-password-secret")));
            using var response = await client.SendAsync(request, timeout.Token);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.AreEqual(
                "remote-worker",
                json.RootElement.GetProperty("sessionState").GetString());
            Assert.AreEqual("person@example.test", jmap.Request?.Authentication.Username);
            Assert.AreEqual("route-password-secret", jmap.Request?.Authentication.Secret);
            Assert.AreEqual(
                requestDocument,
                Encoding.UTF8.GetString(jmap.Request?.Document ?? []));

            await using var countCommand = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE metadata ->> 'layer' = 'presentation') "
                + "FROM gateway_traffic_records WHERE protocol = 'jmap'");
            await using var countReader = await countCommand.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await countReader.ReadAsync(timeout.Token));
            Assert.AreEqual(4L, countReader.GetInt64(0));
            Assert.AreEqual(2L, countReader.GetInt64(1));

            await using var operationCommand = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests LIMIT 1");
            Assert.AreEqual(
                ApplicationOperations.JmapApiProcess,
                await operationCommand.ExecuteScalarAsync(timeout.Token));
            await using var ciphertextCommand = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records WHERE payload_inline IS NOT NULL");
            await using var ciphertextReader = await ciphertextCommand.ExecuteReaderAsync(timeout.Token);
            while (await ciphertextReader.ReadAsync(timeout.Token))
            {
                var ciphertext = ciphertextReader.GetFieldValue<byte[]>(0);
                Assert.IsFalse(
                    Encoding.UTF8.GetString(ciphertext)
                        .Contains("route-password-secret", StringComparison.Ordinal));
            }
        }
        finally
        {
            await application.StopAsync(timeout.Token);
            await application.DisposeAsync();
            await worker.StopAsync(timeout.Token);
            worker.Dispose();
        }
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }

    private sealed class StubJmapApplicationService : IJmapApplicationService
    {
        public JmapApiApplicationRequest? Request { get; private set; }

        public Task<JmapApplicationResult> GetSessionAsync(
            JmapSessionApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> ProcessApiRequestAsync(
            JmapApiApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new JmapApplicationResult(
                JmapApplicationOutcomes.Ok,
                "{\"methodResponses\":[],\"sessionState\":\"remote-worker\"}"u8.ToArray(),
                "application/json"));
        }

        public Task<JmapApplicationResult> UploadAsync(
            JmapUploadApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> DownloadAsync(
            JmapDownloadApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> PollEventAsync(
            JmapEventApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
