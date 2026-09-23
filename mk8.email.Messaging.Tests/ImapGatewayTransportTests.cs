using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Imap;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapGatewayTransportTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task ImapAuthenticationUsesRemoteWorkerAndEncryptedTrafficRecords()
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
            "test", "imap-auth-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "imap-auth-key");
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
        var application = new RecordingImapApplication();
        await using var workerProvider = new ServiceCollection()
            .AddSingleton<IImapApplicationService>(application)
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@imap-test-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var transport = new GatewayApplicationTransport(
            gatewayBus,
            journal,
            new GatewayApplicationOptions(
                "gateway@imap-test-host", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        var client = new GatewayImapApplicationService(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(timeout.Token);
        try
        {
            var password = await client.AuthenticatePasswordAsync(
                new ImapPasswordAuthentication("user@example.test", "imap-secret"), timeout.Token);
            var oauth = await client.AuthenticateOAuthAsync(
                new ImapOAuthAuthentication("user@example.test", "imap-access-token"), timeout.Token);
            Assert.AreEqual(application.UserId, password.UserId);
            Assert.AreEqual(application.UserId, oauth.UserId);
            Assert.AreEqual("imap-secret", application.Password);
            Assert.AreEqual("imap-access-token", application.AccessToken);

            await using var operations = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests ORDER BY created_at");
            await using var operationReader = await operations.ExecuteReaderAsync(timeout.Token);
            var observed = new List<string>();
            while (await operationReader.ReadAsync(timeout.Token))
                observed.Add(operationReader.GetString(0));
            CollectionAssert.AreEqual(
                new[]
                {
                    ApplicationOperations.ImapAuthenticatePassword,
                    ApplicationOperations.ImapAuthenticateOAuth,
                },
                observed);

            await using var records = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE protocol = 'imap' AND application_request_id IS NOT NULL");
            await using var recordReader = await records.ExecuteReaderAsync(timeout.Token);
            var recordCount = 0;
            while (await recordReader.ReadAsync(timeout.Token))
            {
                recordCount++;
                var ciphertext = Encoding.UTF8.GetString(recordReader.GetFieldValue<byte[]>(0));
                Assert.IsFalse(ciphertext.Contains("imap-secret", StringComparison.Ordinal));
                Assert.IsFalse(ciphertext.Contains("imap-access-token", StringComparison.Ordinal));
            }
            Assert.AreEqual(4, recordCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private sealed class RecordingImapApplication : IImapApplicationService
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
        public string? Password { get; private set; }
        public string? AccessToken { get; private set; }

        public Task<ImapIdentityResult> AuthenticatePasswordAsync(
            ImapPasswordAuthentication request,
            CancellationToken cancellationToken = default)
        {
            Password = request.Password;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }

        public Task<ImapIdentityResult> AuthenticateOAuthAsync(
            ImapOAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            AccessToken = request.AccessToken;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }
    }
}
