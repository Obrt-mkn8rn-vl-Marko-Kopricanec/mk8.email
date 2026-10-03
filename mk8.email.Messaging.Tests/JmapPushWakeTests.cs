using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapPushWakeTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The VerifiedSubscriptionNotificationWakesIdleWorkerAndClearsStaleRetry scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task VerifiedSubscriptionNotificationWakesIdleWorkerAndClearsStaleRetry()
    {
        var server = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var serverLifetime = new NullableAsyncDisposable(server).ConfigureAwait(false);
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var connection = new NpgsqlConnectionStringBuilder(server.ConnectionString);
        var environment = new EnvironmentConfig
        {
            Database = new DatabaseConfig
            {
                Host = connection.Host!,
                Port = connection.Port,
                Name = connection.Database!,
                Username = connection.Username!,
                Password = connection.Password!,
            },
        };
        var services = new ServiceCollection()
            .AddDbContext<EmailDbContext>(options => options.UseNpgsql(server.ConnectionString))
            .AddScoped<JmapAccountService>()
            .AddScoped<JmapStateService>()
            .AddScoped<JmapStateChangeService>();
        var provider = services.BuildServiceProvider();
        await using var providerLifetime = provider.ConfigureAwait(false);
        var userId = Guid.CreateVersion7();
        {
            var scope = provider.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            await database.Users.AddAsync(new UserDB
            {
                Id = userId,
                Username = "push-wake@example.test",
                PasswordHash = "unused",
            }).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var worker = new JmapPushWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new UnavailableJmapPushPresentationClient(),
            environment,
            NullLogger<JmapPushWorker>.Instance);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var observer = new NpgsqlConnection(server.ConnectionString);
            await using var observerLifetime = observer.ConfigureAwait(false);
            await observer.OpenAsync(timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var ready = observer.CreateCommand();
                await using var readyLifetime = ready.ConfigureAwait(false);
                ready.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity "
                    + "WHERE datname = @database AND application_name = 'mk8.email' "
                    + "AND query = 'LISTEN mk8_jmap_push_ready' AND state = 'idle')";
                ready.Parameters.AddWithValue("database", connection.Database!);
                if ((bool)(await ready.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) ?? false))
                    break;
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }

            var subscriptionId = Guid.CreateVersion7();
            {
                var scope = provider.CreateAsyncScope();
                await using var scopeLifetime = scope.ConfigureAwait(false);
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                await database.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
                {
                    Id = subscriptionId,
                    SubscriptionObjectId = "push-wake",
                    UserId = userId,
                    DeviceClientId = "wake-device",
                    Url = "https://push.example.test/endpoint",
                    VerificationCode = "code",
                    IsVerified = true,
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                    NextPushAt = DateTime.UtcNow.AddSeconds(-1),
                }).ConfigureAwait(false);
                await database.SaveChangesAsync(timeout.Token).ConfigureAwait(false);
            }

            while (true)
            {
                var scope = provider.CreateAsyncScope();
                await using var scopeLifetime = scope.ConfigureAwait(false);
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var nextPushAt = await database.JmapPushSubscriptions
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == subscriptionId)
                    .Select(candidate => candidate.NextPushAt)
                    .SingleAsync(timeout.Token).ConfigureAwait(false);
                if (nextPushAt is null)
                    break;
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            worker.Dispose();
        }
    }
}
