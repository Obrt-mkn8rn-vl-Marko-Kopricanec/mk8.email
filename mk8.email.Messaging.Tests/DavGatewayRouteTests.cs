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
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Dav;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Dav;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DavGatewayRouteTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The HttpPropfindCrossesSeparateWorkerAndRecordsBothBoundaries scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task HttpPropfindCrossesSeparateWorkerAndRecordsBothBoundaries()
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
            "test", "dav-route-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "dav-route-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gatewayBus = new PostgresApplicationBus(gatewayDataSource, gatewayProtector, options);
        var workerBus = new PostgresApplicationBus(workerDataSource, workerProtector, options);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource, gatewayProtector, options);
        var dispatcher = new StubDavDispatcher();
        var workerProvider = new ServiceCollection()
            .AddSingleton<IApplicationRequestDispatcher>(dispatcher)
            .BuildServiceProvider();
        await using var workerProviderLifetime = workerProvider.ConfigureAwait(false);
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@dav-route-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "email.example.test" },
            Dav = new DavConfig { EnableDav = true, MaxResourceSizeBytes = 65_536 },
        });
        builder.Services.AddSingleton<IApplicationRequestClient>(gatewayBus);
        builder.Services.AddSingleton<IGatewayTrafficJournal>(journal);
        builder.Services.AddGatewayApplicationClient();
        builder.Services.AddScoped<GatewayDavStore>();
        var gateway = builder.Build();
        gateway.UseMiddleware<GatewayProtocolTrafficCaptureMiddleware>();
        gateway.UseMiddleware<GatewayApplicationFailureMiddleware>();
        gateway.UseRouting();
        gateway.MapDavEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        await gateway.StartAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var address = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new AssertFailedException("The DAV Gateway did not publish an address.");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/")
            {
                Content = new StringContent(
                    "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>",
                    Encoding.UTF8,
                    "application/xml"),
            };
            request.Headers.TryAddWithoutValidation("Depth", "0");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "user@example.test:gateway-dav-secret")));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);

            Assert.AreEqual(HttpStatusCode.MultiStatus, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            StringAssert.Contains(body, $"/dav/principals/{dispatcher.UserId:N}/", StringComparison.Ordinal);
            Assert.AreEqual("gateway-dav-secret", dispatcher.Password, StringComparer.Ordinal);
            Assert.AreEqual(dispatcher.UserId, dispatcher.EnsuredUserId);

            var countCommand = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE metadata ->> 'layer' = 'presentation') "
                + "FROM gateway_traffic_records WHERE protocol = 'dav'");
            await using var countCommandLifetime = countCommand.ConfigureAwait(false);
            var countReader = (await countCommand.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var countReaderLifetime = countReader.ConfigureAwait(false);
            Assert.IsTrue(await countReader.ReadAsync(timeout.Token).ConfigureAwait(false));
            Assert.AreEqual(6L, countReader.GetInt64(0));
            Assert.AreEqual(2L, countReader.GetInt64(1));

            var operationsCommand = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests");
            await using var operationsCommandLifetime = operationsCommand.ConfigureAwait(false);
            var operationsReader = (await operationsCommand.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var operationsReaderLifetime = operationsReader.ConfigureAwait(false);
            var operations = new List<string>();
            while (await operationsReader.ReadAsync(timeout.Token).ConfigureAwait(false))
                operations.Add(operationsReader.GetString(0));
            CollectionAssert.AreEquivalent(
                new[] { ApplicationOperations.DavAuthenticate, ApplicationOperations.DavEnsureCollections },
                operations);

            var ciphertextCommand = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records WHERE payload_inline IS NOT NULL");
            await using var ciphertextCommandLifetime = ciphertextCommand.ConfigureAwait(false);
            var ciphertextReader = (await ciphertextCommand.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
            await using var ciphertextReaderLifetime = ciphertextReader.ConfigureAwait(false);
            while (await ciphertextReader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                Assert.IsFalse(Encoding.UTF8.GetString(await (ciphertextReader.GetFieldValueAsync<byte[]>(0)).ConfigureAwait(false))
                    .Contains("gateway-dav-secret", StringComparison.Ordinal));
            }
        }
        finally
        {
            await gateway.StopAsync(timeout.Token).ConfigureAwait(false);
            await gateway.DisposeAsync().ConfigureAwait(false);
            await worker.StopAsync(timeout.Token).ConfigureAwait(false);
            worker.Dispose();
        }
    }

    private sealed class StubDavDispatcher : IApplicationRequestDispatcher
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public Guid UserId { get; } = Guid.CreateVersion7();
        public string? Password { get; private set; }
        public Guid? EnsuredUserId { get; private set; }

        public Task<ApplicationResponse> DispatchAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            object result = request.Operation switch
            {
                ApplicationOperations.DavAuthenticate => Authenticate(request),
                ApplicationOperations.DavEnsureCollections => EnsureCollections(request),
                _ => throw new NotSupportedException(request.Operation),
            };
            return Task.FromResult(new ApplicationResponse(
                request.Id,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions),
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        private DavLookupResult<DavUser> Authenticate(ApplicationRequest request)
        {
            var value = JsonSerializer.Deserialize<DavAuthenticationRequest>(request.Payload, JsonOptions)
                ?? throw new JsonException("Missing DAV authentication request.");
            Assert.AreEqual(ProtocolAuthenticationKinds.Password, value.Authentication.Kind, StringComparer.Ordinal);
            Assert.AreEqual("user@example.test", value.Authentication.Username, StringComparer.Ordinal);
            Password = value.Authentication.Secret;
            return new DavLookupResult<DavUser>(new DavUser(UserId, "user@example.test"));
        }

        private DavAcknowledgement EnsureCollections(ApplicationRequest request)
        {
            var value = JsonSerializer.Deserialize<DavEnsureCollectionsRequest>(request.Payload, JsonOptions)
                ?? throw new JsonException("Missing DAV collection request.");
            EnsuredUserId = value.User.Id;
            return new DavAcknowledgement(true);
        }
    }
}
