using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailPushSubscriptionReader(
    EmailDbContext database,
    EnvironmentConfig environment) : IMailPushSubscriptionReader
{
    public async Task<MailPushSubscriptionReadResult> ReadAsync(
        MailPushSubscriptionReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (command.SubscriptionIds?.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailPushSubscriptionReadStatus.RequestTooLarge, []);

        var now = DateTime.UtcNow;
        var expired = await database.JmapPushSubscriptions
            .Where(subscription => subscription.UserId == user.Id && subscription.ExpiresAt <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count > 0)
        {
            for (var index = 0; index < expired.Count; index++)
            {
                expired[index].Url = string.Empty;
                expired[index].KeysJson = null;
            }
            database.JmapPushSubscriptions.RemoveRange(expired);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var all = await database.JmapPushSubscriptions.AsNoTracking()
            .Where(subscription => subscription.UserId == user.Id)
            .OrderBy(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (command.SubscriptionIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailPushSubscriptionReadStatus.RequestTooLarge, []);
        HashSet<Guid>? requested = command.SubscriptionIds?.ToHashSet();
        var subscriptions = all.Where(subscription => requested is null || requested.Contains(subscription.Id))
            .Select(subscription => new MailPushSubscriptionSnapshot(subscription.Id,
                subscription.DeviceClientId,
                subscription.IsVerified ? subscription.VerificationCode : null,
                subscription.ExpiresAt,
                subscription.Types))
            .ToArray();
        return new(MailPushSubscriptionReadStatus.Ok, subscriptions);
    }
}
