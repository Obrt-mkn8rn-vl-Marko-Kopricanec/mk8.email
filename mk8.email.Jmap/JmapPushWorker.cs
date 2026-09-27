using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Jmap;

internal sealed class JmapPushWorker(
    IServiceScopeFactory scopeFactory,
    IJmapPushPresentationClient delivery,
    EnvironmentConfig environment,
    ILogger<JmapPushWorker> logger) : BackgroundService, IJmapPushWork
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsPostgreSql())
        {
            await RunNotificationLoopAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        await RunPollingLoopAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "JMAP push delivery loop failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunNotificationLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var listener = new NpgsqlConnection(environment.BuildConnectionString());
                await using var listenerLifetime = listener.ConfigureAwait(false);
                await listener.OpenAsync(stoppingToken).ConfigureAwait(false);
                var command = listener.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "LISTEN mk8_jmap_push_ready";
                    await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    await ProcessDueAsync(stoppingToken).ConfigureAwait(false);
                    var delay = await GetNextWakeDelayAsync(stoppingToken).ConfigureAwait(false);
                    await listener.WaitAsync(
                        Math.Max(1, checked((int)delay.TotalMilliseconds)),
                        stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The JMAP push notification listener failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        var subscriptionIds = await GetBatchAsync(cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            subscriptionIds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            ProcessAsync).ConfigureAwait(false);
    }

    private async Task<TimeSpan> GetNextWakeDelayAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var nextRetry = await database.JmapPushSubscriptions
            .Where(subscription => subscription.IsVerified && subscription.NextPushAt != null)
            .MinAsync(subscription => subscription.NextPushAt, cancellationToken).ConfigureAwait(false);
        var nextExpiry = await database.JmapPushSubscriptions
            .MinAsync(subscription => (DateTime?)subscription.ExpiresAt, cancellationToken).ConfigureAwait(false);
        var next = new[] { nextRetry, nextExpiry }
            .Where(candidate => candidate is not null)
            .Min();
        var fallback = TimeSpan.FromMinutes(5);
        if (next is null)
            return fallback;

        var untilDue = next.Value - DateTime.UtcNow;
        if (untilDue <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(10);
        return untilDue < fallback ? untilDue : fallback;
    }

    private bool IsPostgreSql()
    {
        using var scope = scopeFactory.CreateScope();
        return string.Equals(
            scope.ServiceProvider.GetRequiredService<EmailDbContext>().Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<Guid>> GetBatchAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var now = DateTime.UtcNow;
        var expired = await database.JmapPushSubscriptions
            .Where(subscription => subscription.ExpiresAt <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count > 0)
        {
            foreach (var subscription in expired)
            {
                subscription.Url = string.Empty;
                subscription.KeysJson = null;
            }
            database.JmapPushSubscriptions.RemoveRange(expired);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return await database.JmapPushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.IsVerified
                && subscription.ExpiresAt > now
                && (subscription.NextPushAt == null || subscription.NextPushAt <= now))
            .OrderBy(subscription => subscription.NextPushAt)
            .ThenBy(subscription => subscription.UpdatedAt)
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var stateChanges = scope.ServiceProvider.GetRequiredService<JmapStateChangeService>();
        var subscription = await database.JmapPushSubscriptions.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken).ConfigureAwait(false);
        if (subscription is null
            || !subscription.IsVerified
            || subscription.ExpiresAt <= DateTime.UtcNow
            || subscription.NextPushAt > DateTime.UtcNow)
            return;
        var user = await database.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == subscription.UserId && candidate.IsActive)
            .Select(candidate => new AuthenticatedMailUser(candidate.Id, candidate.Username))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            subscription.Url = string.Empty;
            subscription.KeysJson = null;
            database.JmapPushSubscriptions.Remove(subscription);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestedTypes = subscription.Types?.ToHashSet(StringComparer.Ordinal);
        var poll = await stateChanges.PollAsync(
            user,
            subscription.LastPushedChange,
            requestedTypes,
            cancellationToken).ConfigureAwait(false);
        if (poll.Cursor == subscription.LastPushedChange)
        {
            if (subscription.NextPushAt is not null)
            {
                subscription.NextPushAt = null;
                subscription.UpdatedAt = DateTime.UtcNow;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        if (poll.StateChange is null)
        {
            subscription.LastPushedChange = poll.Cursor;
            subscription.FailureCount = 0;
            subscription.NextPushAt = null;
            subscription.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        WebPushSendOutcome result;
        try
        {
            result = await delivery.SendAsync(
                subscription.Url,
                subscription.KeysJson,
                subscription.ExpiresAt,
                JmapPushPresentationPayload.Serialize(poll.StateChange),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not request JMAP Web Push delivery for {SubscriptionId}",
                subscription.Id);
            result = WebPushSendOutcome.Failed;
        }
        var now = DateTime.UtcNow;
        switch (result)
        {
            case WebPushSendOutcome.Success:
                subscription.LastPushedChange = poll.Cursor;
                subscription.FailureCount = 0;
                subscription.NextPushAt = null;
                subscription.UpdatedAt = now;
                break;
            case WebPushSendOutcome.Gone:
                subscription.Url = string.Empty;
                subscription.KeysJson = null;
                database.JmapPushSubscriptions.Remove(subscription);
                break;
            case WebPushSendOutcome.RateLimited:
                subscription.FailureCount++;
                subscription.NextPushAt = now.AddMinutes(
                    Math.Min(60, Math.Pow(2, Math.Min(subscription.FailureCount, 6))));
                subscription.UpdatedAt = now;
                break;
            default:
                subscription.FailureCount++;
                subscription.NextPushAt = now.AddSeconds(
                    Math.Min(900, 5 * Math.Pow(2, Math.Min(subscription.FailureCount, 8))));
                subscription.UpdatedAt = now;
                break;
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
