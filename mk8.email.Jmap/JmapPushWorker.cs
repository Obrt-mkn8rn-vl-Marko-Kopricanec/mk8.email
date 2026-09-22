using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class JmapPushWorker(
    IServiceScopeFactory scopeFactory,
    IJmapPushPresentationClient delivery,
    ILogger<JmapPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var subscriptionIds = await GetBatchAsync(stoppingToken);
                await Parallel.ForEachAsync(
                    subscriptionIds,
                    new ParallelOptions
                    {
                        CancellationToken = stoppingToken,
                        MaxDegreeOfParallelism = 4,
                    },
                    ProcessAsync);
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
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> GetBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var now = DateTime.UtcNow;
        var expired = await database.JmapPushSubscriptions
            .Where(subscription => subscription.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            foreach (var subscription in expired)
            {
                subscription.Url = string.Empty;
                subscription.KeysJson = null;
            }
            database.JmapPushSubscriptions.RemoveRange(expired);
            await database.SaveChangesAsync(cancellationToken);
        }
        return await database.JmapPushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.IsVerified
                && subscription.ExpiresAt > now
                && (subscription.NextPushAt == null || subscription.NextPushAt <= now))
            .OrderBy(subscription => subscription.NextPushAt)
            .ThenBy(subscription => subscription.UpdatedAt)
            .Select(subscription => subscription.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async ValueTask ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var stateChanges = scope.ServiceProvider.GetRequiredService<JmapStateChangeService>();
        var subscription = await database.JmapPushSubscriptions.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (subscription is null
            || !subscription.IsVerified
            || subscription.ExpiresAt <= DateTime.UtcNow
            || subscription.NextPushAt > DateTime.UtcNow)
            return;
        var user = await database.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == subscription.UserId && candidate.IsActive)
            .Select(candidate => new AuthenticatedMailUser(candidate.Id, candidate.Username))
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            subscription.Url = string.Empty;
            subscription.KeysJson = null;
            database.JmapPushSubscriptions.Remove(subscription);
            await database.SaveChangesAsync(cancellationToken);
            return;
        }

        var requestedTypes = subscription.Types?.ToHashSet(StringComparer.Ordinal);
        var poll = await stateChanges.PollAsync(
            user,
            subscription.LastPushedChange,
            requestedTypes,
            cancellationToken);
        if (poll.Cursor == subscription.LastPushedChange)
            return;
        if (poll.StateChange is null)
        {
            subscription.LastPushedChange = poll.Cursor;
            subscription.FailureCount = 0;
            subscription.NextPushAt = null;
            subscription.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
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
                cancellationToken);
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
        await database.SaveChangesAsync(cancellationToken);
    }
}
