using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.OAuth;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class ApplicationRequestWorkerTests
{
    [TestMethod]
    public async Task WorkerSleepsUntilDurableWorkArrivesThenCompletesIt()
    {
        var requests = new StubRequestConsumer();
        var dispatcher = new StubDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IApplicationRequestDispatcher>(_ => dispatcher)
            .BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            requests,
            services.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@test-host", TimeSpan.FromMinutes(1)),
            NullLogger<ApplicationRequestWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(timeout.Token);
        await requests.WaitEntered.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(0, dispatcher.DispatchCount);

        var request = NewRequest();
        await requests.Queue.Writer.WriteAsync(
            new ApplicationRequestLease(
                request,
                "application@test-host",
                DateTimeOffset.UtcNow.AddMinutes(2),
                1),
            timeout.Token);
        var response = await requests.Completed.Task.WaitAsync(timeout.Token);

        Assert.AreEqual(1, dispatcher.DispatchCount);
        Assert.AreEqual(request.Id, response.RequestId);
        await worker.StopAsync(timeout.Token);
        worker.Dispose();
        await services.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task RemoteWorkerConsumesRequestQueuedWhileItWasOffline()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "worker-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "worker-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gateway = new PostgresApplicationBus(gatewayDataSource, gatewayProtector, options);
        var workerBus = new PostgresApplicationBus(workerDataSource, workerProtector, options);
        var request = NewRequest();
        await gateway.EnqueueAsync(request);

        var services = new ServiceCollection();
        services.AddScoped<IApplicationRequestDispatcher>(serviceProvider =>
            new ApplicationRequestDispatcher(serviceProvider));
        await using var provider = services.BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@remote-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(timeout.Token);
        var response = await gateway.WaitForResponseAsync(request.Id, request.Deadline, timeout.Token);

        Assert.AreEqual("application/json", response.ContentType);
        Assert.IsFalse(response.IsError);
        await worker.StopAsync(timeout.Token);
        worker.Dispose();
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task OAuthGatewayCallCrossesRemoteWorkerAndRecordsBothDirections()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "oauth-worker-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "oauth-worker-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gatewayBus = new PostgresApplicationBus(
            gatewayDataSource,
            gatewayProtector,
            options);
        var workerBus = new PostgresApplicationBus(
            workerDataSource,
            workerProtector,
            options);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource,
            gatewayProtector,
            options);
        var oauth = new StubOAuthApplicationService();
        var services = new ServiceCollection()
            .AddSingleton<IOAuthApplicationService>(oauth)
            .AddScoped<IApplicationRequestDispatcher>(serviceProvider =>
                new ApplicationRequestDispatcher(serviceProvider));
        await using var provider = services.BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@oauth-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var transport = new GatewayApplicationTransport(
            gatewayBus,
            journal,
            new GatewayApplicationOptions(
                "gateway@oauth-host",
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(5)));
        var client = new GatewayOAuthClient(transport);
        var request = new OAuthAuthorizeApplicationRequest(
            "person@example.test",
            "secret-password",
            "123456",
            "thunderbird",
            "http://127.0.0.1:49152/",
            "Remote test",
            ["offline_access", "imap"],
            new string('a', 43),
            null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(timeout.Token);
        var result = await client.AuthorizeAsync(request, timeout.Token);

        Assert.AreEqual(OAuthAuthorizationOutcome.Succeeded, result.Outcome);
        Assert.AreEqual("remote-authorization-code", result.AuthorizationCode);
        Assert.AreEqual(request.Username, oauth.AuthorizationRequest?.Username);
        await using var sessionCommand = gatewayDataSource.CreateCommand(
            "SELECT session_id FROM gateway_traffic_records LIMIT 1");
        var sessionId = (Guid)(await sessionCommand.ExecuteScalarAsync(timeout.Token)
            ?? throw new AssertFailedException("The OAuth traffic journal is empty."));
        var records = await journal.ReadSessionAsync(sessionId, timeout.Token);
        Assert.HasCount(2, records);
        Assert.IsTrue(records.All(record => record.Protocol == "oauth"));
        Assert.AreEqual(GatewayTrafficDirections.Inbound, records[0].Direction);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, records[1].Direction);

        await worker.StopAsync(timeout.Token);
        worker.Dispose();
    }

    private static ApplicationRequest NewRequest()
    {
        var now = DateTimeOffset.UtcNow;
        return new ApplicationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "admin",
            ApplicationOperations.SystemPing,
            "application/json",
            "{}"u8.ToArray(),
            new Dictionary<string, string>(),
            now,
            now.AddMinutes(1));
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

    private sealed class StubDispatcher : IApplicationRequestDispatcher
    {
        public int DispatchCount { get; private set; }

        public Task<ApplicationResponse> DispatchAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DispatchCount++;
            return Task.FromResult(new ApplicationResponse(
                request.Id,
                "application/json",
                "{}"u8.ToArray(),
                new Dictionary<string, string>()));
        }
    }

    private sealed class StubRequestConsumer : IApplicationRequestConsumer
    {
        public Channel<ApplicationRequestLease> Queue { get; } = Channel.CreateUnbounded<ApplicationRequestLease>();
        public TaskCompletionSource WaitEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ApplicationResponse> Completed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ApplicationRequestLease> WaitForRequestAsync(
            string workerId,
            CancellationToken cancellationToken = default)
        {
            WaitEntered.TrySetResult();
            return await Queue.Reader.ReadAsync(cancellationToken);
        }

        public Task<ApplicationRequestLease?> TryClaimAsync(
            string workerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ApplicationRequestLease?>(null);

        public Task<bool> RenewLeaseAsync(
            ApplicationRequestLease lease,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task CompleteAsync(
            ApplicationRequestLease lease,
            ApplicationResponse response,
            CancellationToken cancellationToken = default)
        {
            Completed.TrySetResult(response);
            return Task.CompletedTask;
        }

        public Task FailAsync(
            ApplicationRequestLease lease,
            string errorCode,
            string errorDetail,
            CancellationToken cancellationToken = default)
        {
            Completed.TrySetException(new InvalidOperationException(errorDetail));
            return Task.CompletedTask;
        }
    }

    private sealed class StubOAuthApplicationService : IOAuthApplicationService
    {
        public OAuthAuthorizeApplicationRequest? AuthorizationRequest { get; private set; }

        public Task<OAuthPublicKeyValue> GetPublicKeyAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthIdentityLookupResult> AuthenticateIdentityAsync(
            OAuthIdentityLookupRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthAuthorizeApplicationResult> AuthorizeAsync(
            OAuthAuthorizeApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            AuthorizationRequest = request;
            return Task.FromResult(new OAuthAuthorizeApplicationResult(
                OAuthAuthorizationOutcome.Succeeded,
                "remote-authorization-code"));
        }

        public Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(
            OAuthAuthorizationCodeRedeemRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthTokenApplicationResult> RefreshTokenAsync(
            OAuthRefreshTokenRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeTokenAsync(
            OAuthRevokeTokenRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
