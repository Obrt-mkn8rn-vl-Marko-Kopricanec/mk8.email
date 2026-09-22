using System.Net;
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
using mk8.email.Gateway.Protocols.OAuth;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class OAuthGatewayRouteTests
{
    [TestMethod]
    public async Task HttpTokenRequestCrossesRemoteWorkerAndRecordsBothBoundaries()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "oauth-route-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "oauth-route-key");
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
        var oauth = new StubOAuthApplicationService();
        var workerServices = new ServiceCollection()
            .AddSingleton<IOAuthApplicationService>(oauth)
            .AddScoped<IApplicationRequestDispatcher>(serviceProvider =>
                new ApplicationRequestDispatcher(serviceProvider));
        await using var workerProvider = workerServices.BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@route-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "email.example.test" },
            OAuth = new OAuthConfig
            {
                EnableOAuth = true,
                PublicBaseUrl = "https://email.example.test",
                ClientId = "thunderbird",
            },
        });
        builder.Services.AddSingleton<IApplicationRequestClient>(gatewayBus);
        builder.Services.AddSingleton<IGatewayTrafficJournal>(journal);
        builder.Services.AddGatewayApplicationClient();
        builder.Services.AddOAuthProtocol();
        var application = builder.Build();
        application.UseMiddleware<OAuthTrafficCaptureMiddleware>();
        application.UseMiddleware<GatewayApplicationFailureMiddleware>();
        application.UseRouting();
        application.UseRateLimiter();
        application.MapOAuthEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await worker.StartAsync(timeout.Token);
        await application.StartAsync(timeout.Token);
        try
        {
            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new AssertFailedException("The OAuth Gateway did not publish an address.");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var response = await client.PostAsync(
                "/oauth/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = "thunderbird",
                    ["refresh_token"] = "route-refresh-secret",
                }),
                timeout.Token);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.AreEqual(
                "returned-access-token",
                json.RootElement.GetProperty("access_token").GetString());
            Assert.AreEqual("route-refresh-secret", oauth.RefreshRequest?.RefreshToken);

            await using var countCommand = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE metadata ->> 'layer' = 'presentation') "
                + "FROM gateway_traffic_records WHERE protocol = 'oauth'");
            await using var countReader = await countCommand.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await countReader.ReadAsync(timeout.Token));
            Assert.AreEqual(4L, countReader.GetInt64(0));
            Assert.AreEqual(2L, countReader.GetInt64(1));

            await using var operationCommand = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests LIMIT 1");
            Assert.AreEqual(
                ApplicationOperations.OAuthTokenRefresh,
                await operationCommand.ExecuteScalarAsync(timeout.Token));
            await using var ciphertextCommand = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records WHERE payload_inline IS NOT NULL");
            await using var ciphertextReader = await ciphertextCommand.ExecuteReaderAsync(timeout.Token);
            while (await ciphertextReader.ReadAsync(timeout.Token))
            {
                var ciphertext = ciphertextReader.GetFieldValue<byte[]>(0);
                Assert.IsFalse(
                    Encoding.UTF8.GetString(ciphertext)
                        .Contains("route-refresh-secret", StringComparison.Ordinal));
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

    private sealed class StubOAuthApplicationService : IOAuthApplicationService
    {
        public OAuthRefreshTokenRequest? RefreshRequest { get; private set; }

        public Task<OAuthPublicKeyValue> GetPublicKeyAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthIdentityLookupResult> AuthenticateIdentityAsync(
            OAuthIdentityLookupRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthAuthorizeApplicationResult> AuthorizeAsync(
            OAuthAuthorizeApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(
            OAuthAuthorizationCodeRedeemRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthTokenApplicationResult> RefreshTokenAsync(
            OAuthRefreshTokenRequest request,
            CancellationToken cancellationToken = default)
        {
            RefreshRequest = request;
            return Task.FromResult(new OAuthTokenApplicationResult(new OAuthTokenValue(
                Guid.CreateVersion7(),
                "returned-access-token",
                "rotated-refresh-token",
                600,
                "offline_access imap",
                null)));
        }

        public Task RevokeTokenAsync(
            OAuthRevokeTokenRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
