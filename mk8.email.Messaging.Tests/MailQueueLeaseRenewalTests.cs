using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class MailQueueLeaseRenewalTests
{
    [TestMethod]
    [Timeout(45_000)]
    public async Task LongRemoteDeliveryRenewsLeaseAndStopsAfterOwnershipLoss()
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var environment = new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "email.example.test" },
            Queue = new QueueConfig { LeaseSeconds = 30 },
        };
        var relay = new BlockingRelay();
        var services = new ServiceCollection();
        services.AddSingleton(environment);
        services.AddSingleton<ILargeObjectStore>(new InMemoryLargeObjectStore());
        services.AddDbContext<EmailDbContext>(options => options.UseNpgsql(server.ConnectionString));
        services.AddScoped<LargeObjectTransactionEffects>();
        services.AddScoped<MailQueueContentService>();
        services.AddScoped<MailQueueLargeObjectMigrationService>();
        services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
        services.AddScoped<IEmailService, StubEmailService>();
        services.AddSingleton<IMailScanner>(new CleanScanner());
        services.AddSingleton<IOutboundMailRelay>(relay);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        var queueId = Guid.CreateVersion7();
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            await scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>()
                .EnqueueAsync(new MailSubmission(
                    queueId,
                    "sender@example.test",
                    [new MailEnvelopeRecipient("recipient@remote.test", false)],
                    "From: sender@example.test\r\nTo: recipient@remote.test\r\n"
                    + "Subject: lease renewal\r\n\r\nbody\r\n",
                    "192.0.2.1",
                    "sender.example.test",
                    "sender@example.test"));
        }

        var worker = new MailQueueWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            TimeProvider.System,
            NullLogger<MailQueueWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var processing = worker.ProcessNextAsync(timeout.Token);
        await relay.Entered.Task.WaitAsync(timeout.Token);
        DateTime initialExpiry;
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            initialExpiry = (await database.MailQueueMessages.AsNoTracking()
                .SingleAsync(message => message.Id == queueId, timeout.Token)).LeaseExpiresAt!.Value;
        }

        while (true)
        {
            await using var scope = provider.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var expiry = await database.MailQueueMessages.AsNoTracking()
                .Where(message => message.Id == queueId)
                .Select(message => message.LeaseExpiresAt)
                .SingleAsync(timeout.Token);
            if (expiry > initialExpiry.AddSeconds(5))
                break;
            await Task.Delay(100, timeout.Token);
        }

        var replacementToken = Guid.CreateVersion7();
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var message = await database.MailQueueMessages.SingleAsync(
                candidate => candidate.Id == queueId,
                timeout.Token);
            message.LeaseToken = replacementToken;
            message.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(30);
            await database.SaveChangesAsync(timeout.Token);
        }

        await relay.Cancelled.Task.WaitAsync(timeout.Token);
        Assert.IsTrue(await processing.WaitAsync(timeout.Token));
        await using var verification = provider.CreateAsyncScope();
        var retained = await verification.ServiceProvider.GetRequiredService<EmailDbContext>()
            .MailQueueMessages.AsNoTracking().SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Processing, retained.State);
        Assert.AreEqual(replacementToken, retained.LeaseToken);
    }

    private sealed class CleanScanner : IMailScanner
    {
        public Task<MailScanResult> ScanAsync(
            MailScanRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailScanResult(
                "no action", 0, 5, new HashSet<string>(), string.Empty, false, false));
    }

    private sealed class BlockingRelay : IOutboundMailRelay
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OutboundDeliveryResult> RelayAsync(
            string sender,
            string recipient,
            string rawMessage,
            OutboundMailOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            throw new AssertFailedException("The blocked relay unexpectedly completed.");
        }
    }

    private sealed class StubEmailService : IEmailService
    {
        public Task<bool> CanReceiveAsync(
            string recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> DeliverAsync(
            string sender,
            string recipient,
            string rawMessage,
            string folderName = "INBOX",
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<string>? flags = null,
            bool createFolder = false) =>
            throw new AssertFailedException("Remote mail must not use local delivery.");

        public Task<bool> SaveSentCopyAsync(
            string sender,
            string rawMessage,
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
