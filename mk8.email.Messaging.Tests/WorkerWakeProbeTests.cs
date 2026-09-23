using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class WorkerWakeProbeTests
{
    [TestMethod]
    public async Task ProbeFindsOfflineRequestsAndDueOrLeasedMail()
    {
        await using var database = await RequirePostgresAsync();
        await CreateSchemaAsync(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        var probe = new WorkerWakeProbe(dataSource);
        Assert.IsFalse((await probe.ReadAsync()).HasDueWork);

        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "wake-key");
        var bus = new PostgresApplicationBus(dataSource, protector);
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin",
            ApplicationOperations.SystemPing, "application/json", "{}"u8.ToArray(),
            new Dictionary<string, string>(), now, now.AddMinutes(2));
        await bus.EnqueueAsync(request);
        Assert.IsTrue((await probe.ReadAsync()).HasDueWork);

        var lease = await bus.TryClaimAsync("wake@test-host")
            ?? throw new AssertFailedException("The queued request was not claimable.");
        await bus.CompleteAsync(lease, new ApplicationResponse(
            request.Id, "application/json", "{}"u8.ToArray(),
            new Dictionary<string, string>()));
        Assert.IsFalse((await probe.ReadAsync()).HasDueWork);

        var mailId = Guid.CreateVersion7();
        await using (var context = CreateContext(database.ConnectionString))
        {
            context.MailQueueMessages.Add(new MailQueueMessageDB
            {
                Id = mailId,
                EnvelopeSender = "sender@example.test",
                Direction = MailQueueDirections.Inbound,
                State = MailQueueStates.Pending,
                ScanState = MailQueueScanStates.Pending,
                RawMessageSizeBytes = 1,
                RawMessageObjectProvider = "azure-blob",
                RawMessageObjectName = "mail/queue/wake-test/raw.eml",
                RawMessageObjectSha256 = new string('a', 64),
                RawMessageObjectEntityTag = "wake-test",
                NextAttemptAt = DateTime.UtcNow.AddMinutes(2),
            });
            await context.SaveChangesAsync();
        }
        var future = await probe.ReadAsync();
        Assert.IsFalse(future.HasDueWork);
        Assert.IsTrue(future.NextDueAt > DateTimeOffset.UtcNow.AddMinutes(1));

        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.MailQueueMessages.Where(message => message.Id == mailId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    message => message.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        Assert.IsTrue((await probe.ReadAsync()).HasDueWork);

        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.MailQueueMessages.Where(message => message.Id == mailId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(message => message.State, MailQueueStates.Processing)
                    .SetProperty(message => message.LeaseToken, Guid.CreateVersion7())
                    .SetProperty(message => message.LeaseExpiresAt,
                        DateTime.UtcNow.AddMinutes(2)));
        }
        var leased = await probe.ReadAsync();
        Assert.IsFalse(leased.HasDueWork);
        Assert.IsTrue(leased.NextDueAt > DateTimeOffset.UtcNow.AddMinutes(1));

        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.MailQueueMessages.Where(message => message.Id == mailId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    message => message.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        Assert.IsTrue((await probe.ReadAsync()).HasDueWork);
    }

    [TestMethod]
    public async Task ProbeFindsMissedJmapChangesRetriesAndExpiration()
    {
        await using var database = await RequirePostgresAsync();
        await CreateSchemaAsync(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        var probe = new WorkerWakeProbe(dataSource);
        var companyId = Guid.CreateVersion7();
        var addressId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var inboxId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        await using (var context = CreateContext(database.ConnectionString))
        {
            context.Companies.Add(new CompanyDB { Id = companyId, Name = "Wake Test" });
            context.Addresses.Add(new AddressDB
            {
                Id = addressId,
                CompanyId = companyId,
                Domain = "wake.example.test",
                IsActive = true,
            });
            context.Users.Add(new UserDB
            {
                Id = userId,
                CompanyId = companyId,
                Username = "user@wake.example.test",
                PasswordHash = "test",
            });
            context.Inboxes.Add(new InboxDB
            {
                Id = inboxId,
                AddressId = addressId,
                OwnerId = userId,
                Name = "user",
            });
            await context.SaveChangesAsync();
            var cursor = await context.JmapChanges.MaxAsync(change => (long?)change.Sequence) ?? 0;
            context.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
            {
                Id = subscriptionId,
                SubscriptionObjectId = "P" + subscriptionId.ToString("N"),
                UserId = userId,
                DeviceClientId = "wake-test",
                Url = "https://push.example.test/device",
                VerificationCode = "test",
                IsVerified = true,
                LastPushedChange = cursor,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await context.SaveChangesAsync();
        }
        Assert.IsFalse((await probe.ReadAsync()).HasDueWork);

        await using (var context = CreateContext(database.ConnectionString))
        {
            context.JmapChanges.Add(new JmapChangeDB
            {
                AccountId = inboxId,
                DataType = "Email",
                ObjectId = "wake-test",
                ChangeKind = "created",
            });
            await context.SaveChangesAsync();
        }
        Assert.IsTrue((await probe.ReadAsync()).HasDueWork);
        Assert.IsFalse((await new WorkerWakeProbe(dataSource, includeJmap: false)
            .ReadAsync()).HasDueWork);

        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    subscription => subscription.NextPushAt, DateTime.UtcNow.AddMinutes(2)));
        }
        var deferredPush = await probe.ReadAsync();
        Assert.IsFalse(deferredPush.HasDueWork);
        Assert.IsTrue(deferredPush.NextDueAt > DateTimeOffset.UtcNow.AddMinutes(1));

        await using (var context = CreateContext(database.ConnectionString))
        {
            var cursor = await context.JmapChanges.MaxAsync(change => change.Sequence);
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    subscription => subscription.LastPushedChange, cursor));
        }
        Assert.IsFalse((await probe.ReadAsync()).HasDueWork);

        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    subscription => subscription.NextPushAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        Assert.IsTrue((await probe.ReadAsync()).HasDueWork);

        await using (var context = CreateContext(database.ConnectionString))
        {
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(subscription => subscription.NextPushAt, (DateTime?)null)
                    .SetProperty(subscription => subscription.ExpiresAt,
                        DateTime.UtcNow.AddSeconds(-1)));
        }
        Assert.IsTrue((await probe.ReadAsync()).HasDueWork);
    }

    [TestMethod]
    public async Task MonitorCreatesAnIdempotentTriggerForACommittedRequest()
    {
        await using var database = await RequirePostgresAsync();
        await CreateSchemaAsync(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        var directory = Path.Combine(
            Path.GetTempPath(), "mk8-wake-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task monitoring = Task.CompletedTask;
        try
        {
            var trigger = new WorkerWakeTrigger(directory);
            var monitor = new WorkerWakeMonitor(
                dataSource, new WorkerWakeProbe(dataSource), trigger, TimeProvider.System);
            monitoring = monitor.RunAsync(timeout.Token);
            using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "wake-key");
            var bus = new PostgresApplicationBus(dataSource, protector);
            var now = DateTimeOffset.UtcNow;
            await bus.EnqueueAsync(new ApplicationRequest(
                Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin",
                ApplicationOperations.SystemPing, "application/json", "{}"u8.ToArray(),
                new Dictionary<string, string>(), now, now.AddMinutes(2)));
            var path = Path.Combine(directory, "trigger");
            while (!File.Exists(path))
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, timeout.Token);
            }
            trigger.Signal();
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally
        {
            timeout.Cancel();
            try
            {
                await monitoring;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MonitorWakesForScheduledMailWithoutAnotherNotification()
    {
        await using var database = await RequirePostgresAsync();
        await CreateSchemaAsync(database.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        await using (var context = CreateContext(database.ConnectionString))
        {
            context.MailQueueMessages.Add(new MailQueueMessageDB
            {
                Id = Guid.CreateVersion7(),
                EnvelopeSender = "sender@example.test",
                Direction = MailQueueDirections.Inbound,
                State = MailQueueStates.Pending,
                ScanState = MailQueueScanStates.Pending,
                RawMessageSizeBytes = 1,
                RawMessageObjectProvider = "azure-blob",
                RawMessageObjectName = "mail/queue/wake-test/raw.eml",
                RawMessageObjectSha256 = new string('a', 64),
                RawMessageObjectEntityTag = "wake-test",
                NextAttemptAt = DateTime.UtcNow.AddSeconds(1),
            });
            await context.SaveChangesAsync();
        }

        var directory = Path.Combine(
            Path.GetTempPath(), "mk8-wake-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task monitoring = Task.CompletedTask;
        try
        {
            var monitor = new WorkerWakeMonitor(
                dataSource,
                new WorkerWakeProbe(dataSource),
                new WorkerWakeTrigger(directory),
                TimeProvider.System);
            monitoring = monitor.RunAsync(timeout.Token);
            var path = Path.Combine(directory, "trigger");
            while (!File.Exists(path))
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, timeout.Token);
            }
            Assert.IsTrue((await new WorkerWakeProbe(dataSource).ReadAsync()).HasDueWork);
        }
        finally
        {
            timeout.Cancel();
            try
            {
                await monitoring;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static EmailDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static async Task CreateSchemaAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureCreatedAsync();
        await new MailRuntimeSchemaService(context).EnsureAsync();
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration tests require a database.");
        }
        return database;
    }
}
