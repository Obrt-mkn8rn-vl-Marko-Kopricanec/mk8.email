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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class WorkerWakeProbeTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ProbeFindsOfflineRequestsAndDueOrLeasedMail scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ProbeFindsOfflineRequestsAndDueOrLeasedMail()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await CreateSchemaAsync(database.ConnectionString).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
        var probe = new WorkerWakeProbe(dataSource);
        Assert.IsFalse((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "wake-key");
        var bus = new PostgresApplicationBus(dataSource, protector);
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin",
            ApplicationOperations.SystemPing, "application/json", "{}"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddMinutes(2));
        await bus.EnqueueAsync(request).ConfigureAwait(false);
        Assert.IsTrue((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        var lease = await bus.TryClaimAsync("wake@test-host").ConfigureAwait(false)
            ?? throw new AssertFailedException("The queued request was not claimable.");
        await bus.CompleteAsync(lease, new ApplicationResponse(
            request.Id, "application/json", "{}"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal))).ConfigureAwait(false);
        Assert.IsFalse((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        var mailId = Guid.CreateVersion7();
        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await (context.MailQueueMessages.AddAsync(new MailQueueMessageDB
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
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        var future = await probe.ReadAsync().ConfigureAwait(false);
        Assert.IsFalse(future.HasDueWork);
        Assert.IsTrue(future.NextDueAt > DateTimeOffset.UtcNow.AddMinutes(1));

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.MailQueueMessages.Where(message => message.Id == mailId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    message => message.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1))).ConfigureAwait(false);
        }
        Assert.IsTrue((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.MailQueueMessages.Where(message => message.Id == mailId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(message => message.State, MailQueueStates.Processing)
                    .SetProperty(message => message.LeaseToken, Guid.CreateVersion7())
                    .SetProperty(message => message.LeaseExpiresAt,
                        DateTime.UtcNow.AddMinutes(2))).ConfigureAwait(false);
        }
        var leased = await probe.ReadAsync().ConfigureAwait(false);
        Assert.IsFalse(leased.HasDueWork);
        Assert.IsTrue(leased.NextDueAt > DateTimeOffset.UtcNow.AddMinutes(1));

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.MailQueueMessages.Where(message => message.Id == mailId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    message => message.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1))).ConfigureAwait(false);
        }
        Assert.IsTrue((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ProbeFindsMissedJmapChangesRetriesAndExpiration scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ProbeFindsMissedJmapChangesRetriesAndExpiration()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await CreateSchemaAsync(database.ConnectionString).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
        var probe = new WorkerWakeProbe(dataSource);
        var companyId = Guid.CreateVersion7();
        var addressId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var inboxId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await (context.Companies.AddAsync(new CompanyDB { Id = companyId, Name = "Wake Test" })).ConfigureAwait(false);
            await (context.Addresses.AddAsync(new AddressDB
            {
                Id = addressId,
                CompanyId = companyId,
                Domain = "wake.example.test",
                IsActive = true,
            })).ConfigureAwait(false);
            await (context.Users.AddAsync(new UserDB
            {
                Id = userId,
                CompanyId = companyId,
                Username = "user@wake.example.test",
                PasswordHash = "test",
            })).ConfigureAwait(false);
            await (context.Inboxes.AddAsync(new InboxDB
            {
                Id = inboxId,
                AddressId = addressId,
                OwnerId = userId,
                Name = "user",
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
            var cursor = await context.JmapChanges.MaxAsync(change => (long?)change.Sequence).ConfigureAwait(false) ?? 0;
            await (context.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
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
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        Assert.IsFalse((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await (context.JmapChanges.AddAsync(new JmapChangeDB
            {
                AccountId = inboxId,
                DataType = "Email",
                ObjectId = "wake-test",
                ChangeKind = "created",
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        Assert.IsTrue((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);
        Assert.IsFalse((await new WorkerWakeProbe(dataSource, includeJmap: false)
            .ReadAsync().ConfigureAwait(false)).HasDueWork);

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    subscription => subscription.NextPushAt, DateTime.UtcNow.AddMinutes(2))).ConfigureAwait(false);
        }
        var deferredPush = await probe.ReadAsync().ConfigureAwait(false);
        Assert.IsFalse(deferredPush.HasDueWork);
        Assert.IsTrue(deferredPush.NextDueAt > DateTimeOffset.UtcNow.AddMinutes(1));

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            var cursor = await context.JmapChanges.MaxAsync(change => change.Sequence).ConfigureAwait(false);
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    subscription => subscription.LastPushedChange, cursor)).ConfigureAwait(false);
        }
        Assert.IsFalse((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    subscription => subscription.NextPushAt, DateTime.UtcNow.AddSeconds(-1))).ConfigureAwait(false);
        }
        Assert.IsTrue((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);

        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.JmapPushSubscriptions
                .Where(subscription => subscription.Id == subscriptionId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(subscription => subscription.NextPushAt, (DateTime?)null)
                    .SetProperty(subscription => subscription.ExpiresAt,
                        DateTime.UtcNow.AddSeconds(-1))).ConfigureAwait(false);
        }
        Assert.IsTrue((await probe.ReadAsync().ConfigureAwait(false)).HasDueWork);
    }

    [TestMethod]
    public async Task MonitorCreatesAnIdempotentTriggerForACommittedRequest()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await CreateSchemaAsync(database.ConnectionString).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
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
                new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddMinutes(2))).ConfigureAwait(false);
            var path = Path.Combine(directory, "trigger");
            while (!File.Exists(path))
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
            await trigger.SignalAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try
            {
                await monitoring.ConfigureAwait(false);
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
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await CreateSchemaAsync(database.ConnectionString).ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
        {
            var context = CreateContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await (context.MailQueueMessages.AddAsync(new MailQueueMessageDB
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
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
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
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
            Assert.IsTrue((await new WorkerWakeProbe(dataSource).ReadAsync().ConfigureAwait(false)).HasDueWork);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try
            {
                await monitoring.ConfigureAwait(false);
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
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration tests require a database.");
        }
        return database;
    }
}
