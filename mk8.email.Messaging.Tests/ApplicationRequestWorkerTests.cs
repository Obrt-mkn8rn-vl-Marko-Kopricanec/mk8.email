using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.OAuth;
using mk8.email.Hosting;
using mk8.email.Jmap;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ApplicationRequestWorkerTests
{
    [TestMethod]
    public void DrainModeUsesTheSameQueueAndPushWorkersAsHostedMode()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new EnvironmentConfig());
        services.AddApplication();
        services.AddMailApplicationWorker();
        services.AddJmapApplication();
        using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.AreSame(
            provider.GetRequiredService<MailQueueWorker>(),
            hosted.OfType<MailQueueWorker>().Single());
        Assert.IsTrue(ReferenceEquals(
            provider.GetRequiredService<IJmapPushWork>(),
            hosted.Single(service => service is IJmapPushWork)));
    }

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

        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        await requests.WaitEntered.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(0, dispatcher.DispatchCount);

        var request = NewRequest();
        await requests.Queue.Writer.WriteAsync(
            new ApplicationRequestLease(
                request,
                "application@test-host",
                DateTimeOffset.UtcNow.AddMinutes(2),
                1),
            timeout.Token).ConfigureAwait(false);
        var response = await requests.Completed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

        Assert.AreEqual(1, dispatcher.DispatchCount);
        Assert.AreEqual(request.Id, response.RequestId);
        await worker.StopAsync(timeout.Token).ConfigureAwait(false);
        worker.Dispose();
        await services.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task HandlerFailureJoinsLeaseRenewalBeforeReportingFailure()
    {
        var renewalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new StubRequestConsumer
        {
            RenewalHandler = async (_, cancellationToken) =>
            {
                renewalEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    renewalStopped.TrySetResult();
                }
            },
        };
        var services = new ServiceCollection()
            .AddSingleton<IApplicationRequestDispatcher>(
                _ => new ThrowAfterSignalDispatcher(renewalEntered.Task))
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        using var worker = new ApplicationRequestWorker(
            requests,
            services.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@renewal-test", TimeSpan.FromMilliseconds(10)),
            NullLogger<ApplicationRequestWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = NewRequest();

        await worker.ProcessLeaseAsync(new ApplicationRequestLease(
            request,
            "application@renewal-test",
            DateTimeOffset.UtcNow.AddMinutes(2),
            1), timeout.Token).ConfigureAwait(false);

        Assert.IsTrue(renewalStopped.Task.IsCompleted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => requests.Completed.Task.WaitAsync(timeout.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DrainModeProcessesPendingRequestsAndDueMailThenExits()
    {
        var requests = new StubRequestConsumer();
        var dispatcher = new StubDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IApplicationRequestDispatcher>(_ => dispatcher)
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        var identity = new ApplicationWorkerIdentity(
            "application@drain-host",
            TimeSpan.FromMinutes(1));
        using var worker = new ApplicationRequestWorker(
            requests,
            services.GetRequiredService<IServiceScopeFactory>(),
            identity,
            NullLogger<ApplicationRequestWorker>.Instance);
        await requests.Queue.Writer.WriteAsync(new ApplicationRequestLease(
            NewRequest(), identity.WorkerId, DateTimeOffset.UtcNow.AddMinutes(2), 1)).ConfigureAwait(false);
        await requests.Queue.Writer.WriteAsync(new ApplicationRequestLease(
            NewRequest(), identity.WorkerId, DateTimeOffset.UtcNow.AddMinutes(2), 1)).ConfigureAwait(false);
        var mailCalls = 0;
        var cleanupCalls = 0;
        var pushCalls = 0;
        var effectCalls = 0;
        var runner = new WorkerDrainRunner(
            requests,
            worker,
            identity,
            _ => Task.FromResult(++mailCalls <= 2),
            _ =>
            {
                cleanupCalls++;
                return Task.CompletedTask;
            },
            _ =>
            {
                pushCalls++;
                return Task.CompletedTask;
            },
            _ => Task.FromResult(++effectCalls <= 3));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(timeout.Token).ConfigureAwait(false);

        Assert.AreEqual(2, result.ApplicationRequests);
        Assert.AreEqual(2, result.MailMessages);
        Assert.AreEqual(2, dispatcher.DispatchCount);
        Assert.AreEqual(mailCalls, cleanupCalls);
        Assert.AreEqual(mailCalls, pushCalls);
        Assert.AreEqual(mailCalls, effectCalls);
        Assert.AreEqual(5, effectCalls);
        Assert.IsFalse(requests.WaitEntered.Task.IsCompleted);
    }

    [TestMethod]
    public async Task DrainModeRechecksForWorkArrivingDuringIdleCleanup()
    {
        var requests = new StubRequestConsumer();
        var dispatcher = new StubDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IApplicationRequestDispatcher>(_ => dispatcher)
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        var identity = new ApplicationWorkerIdentity(
            "application@drain-host",
            TimeSpan.FromMinutes(1));
        using var worker = new ApplicationRequestWorker(
            requests,
            services.GetRequiredService<IServiceScopeFactory>(),
            identity,
            NullLogger<ApplicationRequestWorker>.Instance);
        var cleanupCalls = 0;
        var runner = new WorkerDrainRunner(
            requests,
            worker,
            identity,
            _ => Task.FromResult(false),
            _ =>
            {
                if (++cleanupCalls == 1)
                {
                    requests.Queue.Writer.TryWrite(new ApplicationRequestLease(
                        NewRequest(), identity.WorkerId, DateTimeOffset.UtcNow.AddMinutes(2), 1));
                }
                return Task.CompletedTask;
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(timeout.Token).ConfigureAwait(false);

        Assert.AreEqual(1, result.ApplicationRequests);
        Assert.AreEqual(0, result.MailMessages);
        Assert.AreEqual(1, dispatcher.DispatchCount);
        Assert.IsFalse(requests.WaitEntered.Task.IsCompleted);
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task RemoteWorkerConsumesRequestQueuedWhileItWasOffline()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSourceLifetime = workerDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "worker-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "worker-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gateway = new PostgresApplicationBus(gatewayDataSource, gatewayProtector, options);
        var workerBus = new PostgresApplicationBus(workerDataSource, workerProtector, options);
        var request = NewRequest();
        await gateway.EnqueueAsync(request).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddScoped<IApplicationRequestDispatcher>(serviceProvider =>
            new ApplicationRequestDispatcher(serviceProvider));
        var provider = services.BuildServiceProvider();
        await using var providerLifetime = provider.ConfigureAwait(false);
        var worker = new ApplicationRequestWorker(
            workerBus,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@remote-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        var response = await gateway.WaitForResponseAsync(request.Id, request.Deadline, timeout.Token).ConfigureAwait(false);

        Assert.AreEqual("application/json", response.ContentType, StringComparer.Ordinal);
        Assert.IsFalse(response.IsError);
        await DistributedApplicationProbe.ProbeAsync(gateway, TimeSpan.FromSeconds(5), timeout.Token).ConfigureAwait(false);
        await worker.StopAsync(timeout.Token).ConfigureAwait(false);
        worker.Dispose();
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task DrainModeClaimsRemoteRequestQueuedWhileApplicationWasOffline()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSourceLifetime = workerDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "drain-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "drain-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gateway = new PostgresApplicationBus(gatewayDataSource, gatewayProtector, options);
        var workerBus = new PostgresApplicationBus(workerDataSource, workerProtector, options);
        var request = NewRequest();
        await gateway.EnqueueAsync(request).ConfigureAwait(false);

        var services = new ServiceCollection()
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
        await using var servicesLifetime = services.ConfigureAwait(false);
        var identity = new ApplicationWorkerIdentity(
            "application@remote-drain-host", TimeSpan.FromSeconds(30));
        using var worker = new ApplicationRequestWorker(
            workerBus,
            services.GetRequiredService<IServiceScopeFactory>(),
            identity,
            NullLogger<ApplicationRequestWorker>.Instance);
        var runner = new WorkerDrainRunner(
            workerBus,
            worker,
            identity,
            _ => Task.FromResult(false),
            _ => Task.CompletedTask);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(timeout.Token).ConfigureAwait(false);
        var response = await gateway.WaitForResponseAsync(
            request.Id, request.Deadline, timeout.Token).ConfigureAwait(false);

        Assert.AreEqual(1, result.ApplicationRequests);
        Assert.AreEqual(0, result.MailMessages);
        Assert.IsFalse(response.IsError);
        Assert.AreEqual("application/json", response.ContentType, StringComparer.Ordinal);
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The OAuthGatewayCallCrossesRemoteWorkerAndRecordsBothDirections scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task OAuthGatewayCallCrossesRemoteWorkerAndRecordsBothDirections()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSourceLifetime = workerDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
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
        var provider = services.BuildServiceProvider();
        await using var providerLifetime = provider.ConfigureAwait(false);
        using var worker = new ApplicationRequestWorker(
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

        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        var result = await client.AuthorizeAsync(request, timeout.Token).ConfigureAwait(false);

        Assert.AreEqual(OAuthAuthorizationOutcome.Succeeded, result.Outcome);
        Assert.AreEqual("remote-authorization-code", result.AuthorizationCode, StringComparer.Ordinal);
        Assert.AreEqual(request.Username, oauth.AuthorizationRequest?.Username, StringComparer.Ordinal);
        var sessionCommand = gatewayDataSource.CreateCommand(
            "SELECT session_id FROM gateway_traffic_records LIMIT 1");
        await using var sessionCommandLifetime = sessionCommand.ConfigureAwait(false);
        var sessionId = (Guid)(await sessionCommand.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new AssertFailedException("The OAuth traffic journal is empty."));
        var records = await journal.ReadSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
        Assert.HasCount(2, records);
        Assert.IsTrue(records.All(record => string.Equals(record.Protocol, "oauth", StringComparison.Ordinal)));
        Assert.AreEqual(GatewayTrafficDirections.Inbound, records[0].Direction, StringComparer.Ordinal);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, records[1].Direction, StringComparer.Ordinal);

        await worker.StopAsync(timeout.Token).ConfigureAwait(false);
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
            new Dictionary<string, string>(StringComparer.Ordinal),
            now,
            now.AddMinutes(1));
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
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
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }

    private sealed class ThrowAfterSignalDispatcher(Task signal) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The handler failed.");
        }
    }

    private sealed class StubRequestConsumer : IApplicationRequestConsumer
    {
        public Channel<ApplicationRequestLease> Queue { get; } = Channel.CreateUnbounded<ApplicationRequestLease>();
        public TaskCompletionSource WaitEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ApplicationResponse> Completed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<ApplicationRequestLease, CancellationToken, Task<bool>>? RenewalHandler { get; init; }

        public async Task<ApplicationRequestLease> WaitForRequestAsync(
            string workerId,
            CancellationToken cancellationToken = default)
        {
            WaitEntered.TrySetResult();
            return await Queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<ApplicationRequestLease?> TryClaimAsync(
            string workerId,
            CancellationToken cancellationToken = default)
        {
            Queue.Reader.TryRead(out var lease);
            return Task.FromResult<ApplicationRequestLease?>(lease);
        }

        public Task<bool> RenewLeaseAsync(
            ApplicationRequestLease lease,
            CancellationToken cancellationToken = default) =>
            RenewalHandler?.Invoke(lease, cancellationToken) ?? Task.FromResult(true);

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
