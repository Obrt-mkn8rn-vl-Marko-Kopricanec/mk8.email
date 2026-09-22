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
public sealed class JmapPushWakeTests
{
    [TestMethod]
    public async Task VerifiedSubscriptionNotificationWakesIdleWorkerAndClearsStaleRetry()
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
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
        await using var provider = services.BuildServiceProvider();
        var userId = Guid.CreateVersion7();
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            database.Users.Add(new UserDB
            {
                Id = userId,
                Username = "push-wake@example.test",
                PasswordHash = "unused",
            });
            await database.SaveChangesAsync();
        }

        var worker = new JmapPushWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new UnavailableJmapPushPresentationClient(),
            environment,
            NullLogger<JmapPushWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var observer = new NpgsqlConnection(server.ConnectionString);
            await observer.OpenAsync(timeout.Token);
            while (true)
            {
                await using var ready = observer.CreateCommand();
                ready.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity "
                    + "WHERE datname = @database AND application_name = 'mk8.email' "
                    + "AND query = 'LISTEN mk8_jmap_push_ready' AND state = 'idle')";
                ready.Parameters.AddWithValue("database", connection.Database!);
                if ((bool)(await ready.ExecuteScalarAsync(timeout.Token) ?? false))
                    break;
                await Task.Delay(50, timeout.Token);
            }

            var subscriptionId = Guid.CreateVersion7();
            await using (var scope = provider.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
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
                });
                await database.SaveChangesAsync(timeout.Token);
            }

            while (true)
            {
                await using var scope = provider.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var nextPushAt = await database.JmapPushSubscriptions
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == subscriptionId)
                    .Select(candidate => candidate.NextPushAt)
                    .SingleAsync(timeout.Token);
                if (nextPushAt is null)
                    break;
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }
}
