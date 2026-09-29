using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailPushSubscriptionMutationService(
    EmailDbContext database,
    JmapStateChangeService stateChanges,
    IJmapPushPresentationClient delivery) : IMailPushSubscriptionMutationService
{
    private const int MaximumSubscriptionsPerUser = 16;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);

    public async Task<MailPushMutationExecution> MutateAsync(
        MailPushSubscriptionMutationCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Creates is null || command.Updates is null || command.Destroys is null)
            throw new InvalidOperationException("The push mutation command is incomplete.");
        var created = new List<MailPushSubscriptionCreateOutcome>(command.Creates.Count);
        var updated = new List<MailPushSubscriptionUpdateOutcome>(command.Updates.Count);
        var destroyed = new List<MailPushSubscriptionDestroyOutcome>(command.Destroys.Count);
        var verifications = new List<MailPushVerification>();
        var createdIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await CreateAllAsync(command.Creates, user, created, verifications, createdIds, cancellationToken)
            .ConfigureAwait(false);
        await UpdateAllAsync(command.Updates, user, updated, createdIds, cancellationToken)
            .ConfigureAwait(false);
        await DestroyAllAsync(command.Destroys, user, destroyed, createdIds, cancellationToken)
            .ConfigureAwait(false);
        return new(new(created.ToArray(), updated.ToArray(), destroyed.ToArray()), verifications.ToArray());
    }

    private async Task CreateAllAsync(IReadOnlyList<MailPushSubscriptionCreate> commands,
        AuthenticatedMailUser user, List<MailPushSubscriptionCreateOutcome> outcomes,
        List<MailPushVerification> verifications, Dictionary<string, Guid> createdIds,
        CancellationToken cancellationToken)
    {
        foreach (var item in commands)
        {
            if (item is null || item.CreationId is null)
                throw new InvalidOperationException("The push creation command is incomplete.");
            var (subscription, error) = await CreateAsync(item.Values, user, cancellationToken)
                .ConfigureAwait(false);
            if (subscription is null)
            {
                outcomes.Add(new(item.CreationId, null, error, null, null, null));
                continue;
            }
            createdIds[item.CreationId] = subscription.Id;
            outcomes.Add(new(item.CreationId, subscription.Id, MailPushSubscriptionMutationError.None,
                subscription.ExpiresAt, item.Values!.Keys, subscription.Types));
            verifications.Add(new(subscription.Url, subscription.KeysJson, subscription.ExpiresAt,
                new JmapPushMessage(subscription.SubscriptionObjectId, subscription.VerificationCode)));
        }
    }

    private async Task UpdateAllAsync(IReadOnlyList<MailPushSubscriptionUpdate> commands,
        AuthenticatedMailUser user, List<MailPushSubscriptionUpdateOutcome> outcomes,
        Dictionary<string, Guid> createdIds, CancellationToken cancellationToken)
    {
        foreach (var item in commands)
        {
            if (item is null || item.RequestedId is null || item.Target is null)
                throw new InvalidOperationException("The push update command is incomplete.");
            var targetId = Resolve(item.Target, createdIds);
            var subscription = targetId is { } id
                ? await database.JmapPushSubscriptions.FirstOrDefaultAsync(candidate =>
                    candidate.Id == id && candidate.UserId == user.Id, cancellationToken).ConfigureAwait(false)
                : null;
            if (subscription is null || subscription.ExpiresAt <= DateTime.UtcNow)
            {
                outcomes.Add(new(item.RequestedId, null, MailPushSubscriptionMutationError.NotFound, null, null));
                continue;
            }
            if (item.Patch is null)
            {
                outcomes.Add(new(item.RequestedId, null, MailPushSubscriptionMutationError.Skipped, null, null));
                continue;
            }
            var (error, revised, invalid) = ApplyPatch(subscription, item.Patch);
            if (error != MailPushSubscriptionMutationError.None)
            {
                outcomes.Add(new(item.RequestedId, null, error, null, invalid));
                continue;
            }
            subscription.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(item.RequestedId, subscription.Id, error, revised, null));
        }
    }

    private async Task DestroyAllAsync(IReadOnlyList<MailPushSubscriptionDestroy> commands,
        AuthenticatedMailUser user, List<MailPushSubscriptionDestroyOutcome> outcomes,
        Dictionary<string, Guid> createdIds, CancellationToken cancellationToken)
    {
        foreach (var item in commands)
        {
            if (item is null || item.RequestedId is null || item.Target is null)
                throw new InvalidOperationException("The push deletion command is incomplete.");
            var targetId = Resolve(item.Target, createdIds);
            var subscription = targetId is { } id
                ? await database.JmapPushSubscriptions.FirstOrDefaultAsync(candidate =>
                    candidate.Id == id && candidate.UserId == user.Id, cancellationToken).ConfigureAwait(false)
                : null;
            if (subscription is null)
            {
                outcomes.Add(new(item.RequestedId, null, MailPushSubscriptionMutationError.NotFound));
                continue;
            }
            subscription.Url = string.Empty;
            subscription.KeysJson = null;
            database.JmapPushSubscriptions.Remove(subscription);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(item.RequestedId, subscription.Id, MailPushSubscriptionMutationError.None));
        }
    }

    private async Task<(JmapPushSubscriptionDB? Subscription, MailPushSubscriptionMutationError Error)>
        CreateAsync(MailPushSubscriptionCreateValues? values, AuthenticatedMailUser user,
            CancellationToken cancellationToken)
    {
        if (values is null) return (null, MailPushSubscriptionMutationError.Skipped);
        if (values.DeviceClientId is null || values.DeviceClientId.Length is < 1 or > 255
            || values.Url is null || values.Url.Length > 2048
            || !TryKeys(values.Keys, out var keysJson)
            || !TryTypes(values.Types, out var types)
            || !await delivery.IsSafeUrlAsync(values.Url, cancellationToken).ConfigureAwait(false))
            return (null, MailPushSubscriptionMutationError.InvalidProperties);
        var now = DateTime.UtcNow;
        if (values.Expires is { } requested && requested.UtcDateTime <= now)
            return (null, MailPushSubscriptionMutationError.InvalidProperties);
        var active = await database.JmapPushSubscriptions.CountAsync(candidate =>
            candidate.UserId == user.Id && candidate.ExpiresAt > now, cancellationToken).ConfigureAwait(false);
        if (active >= MaximumSubscriptionsPerUser)
            return (null, MailPushSubscriptionMutationError.OverQuota);
        var latest = await database.JmapPushSubscriptions
            .Where(candidate => candidate.UserId == user.Id)
            .MaxAsync(candidate => (DateTime?)candidate.CreatedAt, cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest > now.AddSeconds(-1))
            return (null, MailPushSubscriptionMutationError.RateLimit);
        var id = Guid.CreateVersion7();
        var subscription = new JmapPushSubscriptionDB
        {
            Id = id,
            SubscriptionObjectId = $"P{id:N}",
            UserId = user.Id,
            DeviceClientId = values.DeviceClientId,
            Url = values.Url,
            Types = types,
            KeysJson = keysJson,
            VerificationCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            IsVerified = false,
            ExpiresAt = LimitExpiry(values.Expires, now),
            CreatedAt = now,
            UpdatedAt = now,
            LastPushedChange = await stateChanges.GetCursorAsync(user, cancellationToken).ConfigureAwait(false),
        };
        await database.JmapPushSubscriptions.AddAsync(subscription, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (subscription, MailPushSubscriptionMutationError.None);
    }

    private static (MailPushSubscriptionMutationError Error, DateTime? RevisedExpiry,
        IReadOnlyList<string>? InvalidProperties) ApplyPatch(
        JmapPushSubscriptionDB subscription, MailPushSubscriptionPatch patch)
    {
        if (patch.UnknownProperties is null)
            throw new InvalidOperationException("The push mutation patch is incomplete.");
        if ((patch.AssertKeyP256dh || patch.AssertKeyAuth || patch.HasUnsupportedKeysPath)
            && subscription.KeysJson is null)
            return (MailPushSubscriptionMutationError.InvalidPatch, null, null);
        var invalid = new HashSet<string>(patch.UnknownProperties, StringComparer.Ordinal);
        if (patch.AssertId && !string.Equals(patch.Id, subscription.SubscriptionObjectId, StringComparison.Ordinal))
            invalid.Add("id");
        if (patch.AssertDeviceClientId && !string.Equals(patch.DeviceClientId, subscription.DeviceClientId,
                StringComparison.Ordinal)) invalid.Add("deviceClientId");
        if (patch.AssertUrl && !string.Equals(patch.Url, subscription.Url, StringComparison.Ordinal))
            invalid.Add("url");
        var storedKeys = ReadStoredKeys(subscription.KeysJson);
        if (patch.AssertKeys && !Equals(patch.Keys, storedKeys)
            || patch.AssertKeyP256dh && !string.Equals(patch.KeyP256dh, storedKeys?.P256dh,
                StringComparison.Ordinal)
            || patch.AssertKeyAuth && !string.Equals(patch.KeyAuth, storedKeys?.Auth,
                StringComparison.Ordinal)
            || patch.ChangesUnsupportedKeys) invalid.Add("keys");
        var verified = subscription.IsVerified;
        if (patch.SetVerificationCode)
        {
            if (patch.VerificationCode is null && !verified)
            {
                // A full /get object has verificationCode:null until verification.
            }
            else if (!string.Equals(patch.VerificationCode, subscription.VerificationCode,
                         StringComparison.Ordinal)) invalid.Add("verificationCode");
            else verified = true;
        }
        var expiresAt = subscription.ExpiresAt;
        DateTime? revised = null;
        if (patch.SetExpires)
        {
            var limited = LimitExpiry(patch.Expires, DateTime.UtcNow);
            if (limited <= DateTime.UtcNow) invalid.Add("expires");
            else
            {
                expiresAt = limited;
                if (patch.Expires is null || limited != patch.Expires.Value.UtcDateTime)
                    revised = limited;
            }
        }
        var types = subscription.Types;
        if (patch.SetTypes && !TryTypes(patch.Types, out types)) invalid.Add("types");
        if (invalid.Count > 0)
            return (MailPushSubscriptionMutationError.InvalidProperties, null,
                invalid.Order(StringComparer.Ordinal).ToArray());
        subscription.IsVerified = verified;
        subscription.ExpiresAt = expiresAt;
        subscription.Types = types;
        return (MailPushSubscriptionMutationError.None, revised, null);
    }

    private static Guid? Resolve(MailPushSubscriptionTarget target,
        Dictionary<string, Guid> createdIds) => target.CreatedKey is { } key
            ? createdIds.GetValueOrDefault(key) is { } id && id != Guid.Empty ? id : null
            : target.ExistingId;

    private static bool TryTypes(IReadOnlyList<string>? values, out string[]? types)
    {
        types = null;
        if (values is null) return true;
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || !JmapStateChangeService.SupportedTypes.Contains(value)) return false;
            parsed.Add(value);
        }
        types = parsed.ToArray();
        return true;
    }

    private static bool TryKeys(MailPushSubscriptionKeys? keys, out string? keysJson)
    {
        keysJson = null;
        if (keys is null) return true;
        if (keys.P256dh is null || keys.Auth is null
            || !JmapPushKeyValidator.TryValidate(keys.P256dh, keys.Auth)) return false;
        keysJson = new JsonObject { ["p256dh"] = keys.P256dh, ["auth"] = keys.Auth }
            .ToJsonString(JmapJson.SerializerOptions);
        return true;
    }

    private static MailPushSubscriptionKeys? ReadStoredKeys(string? json)
    {
        if (json is null) return null;
        var value = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Stored push keys are invalid.");
        var p256dh = value["p256dh"]?.GetValue<string>();
        var auth = value["auth"]?.GetValue<string>();
        if (p256dh is null || auth is null)
            throw new InvalidOperationException("Stored push keys are incomplete.");
        return new(p256dh, auth);
    }

    private static DateTime LimitExpiry(DateTimeOffset? requested, DateTime now)
    {
        var maximum = now.Add(MaximumLifetime);
        return requested is null || requested.Value.UtcDateTime > maximum
            ? maximum : requested.Value.UtcDateTime;
    }
}
