using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class PushSubscriptionGetMethod(
    EmailDbContext database,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> Arguments = new HashSet<string>(
        ["ids", "properties"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        ["id", "deviceClientId", "url", "keys", "verificationCode", "expires", "types"],
        StringComparer.Ordinal);

    public string Name => "PushSubscription/get";
    public string Capability => JmapConstants.CoreCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Any(argument => !Arguments.Contains(argument.Key))
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var requestedProperties)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", true, out var requestedIds))
            return JmapMethodResponse.Error("invalidArguments");
        var properties = requestedProperties?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");
        if (properties is not null && (properties.Contains("url") || properties.Contains("keys")))
            return JmapMethodResponse.Error("forbidden");
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");

        var now = DateTime.UtcNow;
        var expired = await database.JmapPushSubscriptions
            .Where(subscription => subscription.UserId == context.User.Id
                && subscription.ExpiresAt <= now)
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
        var subscriptions = await database.JmapPushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserId == context.User.Id)
            .OrderBy(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken);
        if (requestedIds is null && subscriptions.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = subscriptions.ToDictionary(
            subscription => JmapId.PushSubscription(subscription.Id),
            StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var subscription))
                list.Add(ToJson(subscription, properties));
            else
                notFound.Add(id);
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject ToJson(
        JmapPushSubscriptionDB subscription,
        IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = JmapId.PushSubscription(subscription.Id) };
        if (Wants("deviceClientId")) result["deviceClientId"] = subscription.DeviceClientId;
        if (Wants("verificationCode"))
            result["verificationCode"] = subscription.IsVerified ? subscription.VerificationCode : null;
        if (Wants("expires")) result["expires"] = FormatDate(subscription.ExpiresAt);
        if (Wants("types"))
            result["types"] = subscription.Types is null
                ? null
                : JmapMethodHelpers.ToJsonArray(subscription.Types);
        return result;

        bool Wants(string property) => properties is null || properties.Contains(property);
    }

    internal static string FormatDate(DateTime value) => JmapDate.FormatUtc(value);
}

internal sealed class PushSubscriptionSetMethod(
    EmailDbContext database,
    JmapStateChangeService stateChanges,
    IJmapPushPresentationClient delivery,
    EnvironmentConfig environment,
    ILogger<PushSubscriptionSetMethod> logger) : IJmapMethod
{
    private const int MaximumSubscriptionsPerUser = 16;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);
    private static readonly IReadOnlySet<string> CreateProperties = new HashSet<string>(
        ["deviceClientId", "url", "keys", "verificationCode", "expires", "types"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> UpdateProperties = new HashSet<string>(
        ["verificationCode", "expires", "types"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Arguments = new HashSet<string>(
        ["create", "update", "destroy"],
        StringComparer.Ordinal);

    public string Name => "PushSubscription/set";
    public string Capability => JmapConstants.CoreCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Any(argument => !Arguments.Contains(argument.Key))
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "create", false, out var create)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "update", false, out var update)
            || !TryDestroy(arguments, out var destroy)
            || !JmapMethodHelpers.AreValidCreationIds(create?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(update?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(destroy))
            return JmapMethodResponse.Error("invalidArguments");
        var operationCount = (create?.Count ?? 0) + (update?.Count ?? 0) + (destroy?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");

        var created = new JsonObject();
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();

        if (create is not null)
        {
            foreach (var item in create)
            {
                var result = await CreateAsync(context, item.Key, item.Value, cancellationToken);
                if (result.Error is not null)
                {
                    notCreated[item.Key] = result.Error;
                    continue;
                }
                var subscription = result.Subscription!;
                var id = JmapId.PushSubscription(subscription.Id);
                context.CreatedIds[item.Key] = id;
                created[item.Key] = BuildCreatedResponse(item.Value, subscription);
                var verification = new JsonObject
                {
                    ["@type"] = "PushVerification",
                    ["pushSubscriptionId"] = id,
                    ["verificationCode"] = subscription.VerificationCode,
                };
                context.AddPostCommitAction(async postCommitCancellationToken =>
                {
                    try
                    {
                        await delivery.EnqueueVerificationAsync(
                            subscription.Url,
                            subscription.KeysJson,
                            subscription.ExpiresAt,
                            JmapPushPresentationPayload.Serialize(verification),
                            postCommitCancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not deliver JMAP push verification for {PushSubscriptionId}",
                            id);
                    }
                });
            }
        }

        if (update is not null)
        {
            foreach (var item in update)
            {
                var requestedId = item.Key;
                var resolvedId = context.ResolveId(requestedId);
                if (!JmapId.TryParsePushSubscription(resolvedId, out var id))
                {
                    notUpdated[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var subscription = await database.JmapPushSubscriptions.SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.UserId == context.User.Id,
                    cancellationToken);
                if (subscription is null || subscription.ExpiresAt <= DateTime.UtcNow)
                {
                    notUpdated[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var current = BuildUpdateSource(subscription);
                if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                        current,
                        item.Value,
                        UpdateProperties,
                        out var patched,
                        out var invalidProperties))
                {
                    notUpdated[requestedId] = JmapMethodHelpers.SetError("invalidPatch");
                    continue;
                }
                if (invalidProperties.Count > 0)
                {
                    notUpdated[requestedId] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: invalidProperties);
                    continue;
                }
                var changedProperties = item.Value.KeysForPatch().ToHashSet(StringComparer.Ordinal);
                changedProperties.IntersectWith(UpdateProperties);
                // A full object returned by /get contains verificationCode:null
                // until verification. Under PatchObject null semantics this is
                // a no-op, not an attempt to verify with an invalid code.
                if (!subscription.IsVerified
                    && !patched.ContainsKey("verificationCode"))
                {
                    changedProperties.Remove("verificationCode");
                }
                if (!TryUpdate(
                        subscription,
                        patched,
                        changedProperties,
                        out var revised,
                        out invalidProperties))
                {
                    notUpdated[requestedId] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: invalidProperties);
                    continue;
                }
                subscription.UpdatedAt = DateTime.UtcNow;
                await database.SaveChangesAsync(cancellationToken);
                updated[resolvedId!] = revised.Count == 0 ? null : revised;
            }
        }

        if (destroy is not null)
        {
            foreach (var requestedId in destroy.Distinct(StringComparer.Ordinal))
            {
                var resolvedId = context.ResolveId(requestedId);
                if (!JmapId.TryParsePushSubscription(resolvedId, out var id))
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var subscription = await database.JmapPushSubscriptions.SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.UserId == context.User.Id,
                    cancellationToken);
                if (subscription is null)
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                subscription.Url = string.Empty;
                subscription.KeysJson = null;
                database.JmapPushSubscriptions.Remove(subscription);
                await database.SaveChangesAsync(cancellationToken);
                destroyed.Add(resolvedId);
            }
        }

        return new JmapMethodResponse(Name, new JsonObject
        {
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static JsonObject BuildCreatedResponse(
        JsonObject requested,
        JmapPushSubscriptionDB subscription)
    {
        var response = new JsonObject
        {
            ["id"] = JmapId.PushSubscription(subscription.Id),
        };
        if (!requested.ContainsKey("keys"))
        {
            response["keys"] = subscription.KeysJson is null
                ? null
                : JsonNode.Parse(subscription.KeysJson);
        }
        if (!requested.TryGetPropertyValue("expires", out var requestedExpiryNode)
            || requestedExpiryNode is null
            || requestedExpiryNode is not JsonValue requestedExpiryValue
            || !requestedExpiryValue.TryGetValue<string>(out var requestedExpiry)
            || !JmapDate.TryParseUtcDate(requestedExpiry, out var parsedExpiry)
            || parsedExpiry.UtcDateTime != subscription.ExpiresAt)
        {
            response["expires"] = PushSubscriptionGetMethod.FormatDate(subscription.ExpiresAt);
        }
        if (!requested.ContainsKey("types"))
        {
            response["types"] = subscription.Types is null
                ? null
                : JmapMethodHelpers.ToJsonArray(subscription.Types);
        }
        return response;
    }

    private async Task<PushCreateResult> CreateAsync(
        JmapInvocationContext context,
        string creationId,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        if (!JmapId.IsValidId(creationId)
            || value.Any(property => !CreateProperties.Contains(property.Key))
            || !JmapMethodHelpers.TryGetRequiredString(value, "deviceClientId", out var deviceClientId)
            || deviceClientId.Length is < 1 or > 255
            || !JmapMethodHelpers.TryGetRequiredString(value, "url", out var url)
            || url.Length > 2048
            || value.TryGetPropertyValue("verificationCode", out var verificationNode)
                && verificationNode is not null)
            return PushCreateResult.Failed("invalidProperties");
        if (!TryTypes(value, out var types)
            || !TryExpiry(value, out var expiry)
            || !TryKeys(value, out var keysJson))
            return PushCreateResult.Failed("invalidProperties");
        if (!await delivery.IsSafeUrlAsync(url, cancellationToken))
            return PushCreateResult.Failed("invalidProperties");

        var now = DateTime.UtcNow;
        if (expiry is not null && expiry.Value.UtcDateTime <= now)
            return PushCreateResult.Failed("invalidProperties");
        var existingCount = await database.JmapPushSubscriptions.CountAsync(
            subscription => subscription.UserId == context.User.Id
                && subscription.ExpiresAt > now,
            cancellationToken);
        if (existingCount >= MaximumSubscriptionsPerUser)
            return PushCreateResult.Failed("overQuota");
        var mostRecent = await database.JmapPushSubscriptions
            .Where(subscription => subscription.UserId == context.User.Id)
            .MaxAsync(subscription => (DateTime?)subscription.CreatedAt, cancellationToken);
        if (mostRecent is not null && mostRecent > now.AddSeconds(-1))
            return PushCreateResult.Failed("rateLimit");

        var id = Guid.CreateVersion7();
        var subscription = new JmapPushSubscriptionDB
        {
            Id = id,
            SubscriptionObjectId = JmapId.PushSubscription(id),
            UserId = context.User.Id,
            DeviceClientId = deviceClientId,
            Url = url,
            Types = types,
            KeysJson = keysJson,
            VerificationCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            IsVerified = false,
            ExpiresAt = LimitExpiry(expiry, now),
            CreatedAt = now,
            UpdatedAt = now,
            LastPushedChange = await stateChanges.GetCursorAsync(context.User, cancellationToken),
        };
        database.JmapPushSubscriptions.Add(subscription);
        await database.SaveChangesAsync(cancellationToken);
        return new PushCreateResult(subscription, null);
    }

    private static JsonObject BuildUpdateSource(JmapPushSubscriptionDB subscription) => new()
    {
        ["id"] = JmapId.PushSubscription(subscription.Id),
        ["deviceClientId"] = subscription.DeviceClientId,
        ["url"] = subscription.Url,
        ["keys"] = subscription.KeysJson is null ? null : JsonNode.Parse(subscription.KeysJson),
        ["verificationCode"] = subscription.IsVerified
            ? subscription.VerificationCode
            : null,
        ["expires"] = PushSubscriptionGetMethod.FormatDate(subscription.ExpiresAt),
        ["types"] = subscription.Types is null
            ? null
            : JmapMethodHelpers.ToJsonArray(subscription.Types),
    };

    private static bool TryUpdate(
        JmapPushSubscriptionDB subscription,
        JsonObject value,
        IReadOnlySet<string> changedProperties,
        out JsonObject revised,
        out IReadOnlyList<string> invalidProperties)
    {
        revised = new JsonObject();
        var invalid = new List<string>();
        var verified = subscription.IsVerified;
        var expiresAt = subscription.ExpiresAt;
        var subscriptionTypes = subscription.Types;
        if (changedProperties.Contains("verificationCode"))
        {
            if (!value.TryGetPropertyValue("verificationCode", out var verificationNode)
                || verificationNode is not JsonValue verificationValue
                || !verificationValue.TryGetValue<string>(out var code)
                || code != subscription.VerificationCode)
                invalid.Add("verificationCode");
            else
                verified = true;
        }
        if (changedProperties.Contains("expires"))
        {
            if (!TryExpiry(value, out var expiry))
                invalid.Add("expires");
            else
            {
                var limited = LimitExpiry(expiry, DateTime.UtcNow);
                if (limited <= DateTime.UtcNow)
                    invalid.Add("expires");
                else
                {
                    expiresAt = limited;
                    if (expiry is null || limited != expiry.Value.UtcDateTime)
                        revised["expires"] = PushSubscriptionGetMethod.FormatDate(limited);
                }
            }
        }
        if (changedProperties.Contains("types"))
        {
            if (!TryTypes(value, out var types))
                invalid.Add("types");
            else
                subscriptionTypes = types;
        }
        invalidProperties = invalid.Distinct(StringComparer.Ordinal).ToArray();
        if (invalidProperties.Count > 0)
            return false;

        subscription.IsVerified = verified;
        subscription.ExpiresAt = expiresAt;
        subscription.Types = subscriptionTypes;
        return true;
    }

    private static bool TryExpiry(JsonObject value, out DateTimeOffset? expiry)
    {
        expiry = null;
        if (!value.TryGetPropertyValue("expires", out var node) || node is null)
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || !JmapDate.TryParseUtcDate(text, out var parsed))
            return false;
        expiry = parsed;
        return true;
    }

    private static DateTime LimitExpiry(DateTimeOffset? requested, DateTime now)
    {
        var maximum = now.Add(MaximumLifetime);
        if (requested is null || requested.Value.UtcDateTime > maximum)
            return maximum;
        return requested.Value.UtcDateTime;
    }

    private static bool TryTypes(JsonObject value, out string[]? types)
    {
        types = null;
        if (!value.TryGetPropertyValue("types", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var type)
                || type is null
                || !JmapStateChangeService.SupportedTypes.Contains(type))
                return false;
            parsed.Add(type);
        }
        types = parsed.ToArray();
        return true;
    }

    private static bool TryKeys(JsonObject value, out string? keysJson)
    {
        keysJson = null;
        if (!value.TryGetPropertyValue("keys", out var node) || node is null)
            return true;
        if (node is not JsonObject keys
            || keys.Count != 2
            || !JmapMethodHelpers.TryGetRequiredString(keys, "p256dh", out var p256dh)
            || !JmapMethodHelpers.TryGetRequiredString(keys, "auth", out var auth)
            || !JmapPushKeyValidator.TryValidate(p256dh, auth))
            return false;
        keysJson = new JsonObject
        {
            ["p256dh"] = p256dh,
            ["auth"] = auth,
        }.ToJsonString(JmapJson.SerializerOptions);
        return true;
    }

    private static bool TryDestroy(JsonObject arguments, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null) return true;
        if (node is not JsonArray array) return false;
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || id is null)
                return false;
            result.Add(id);
        }
        values = result;
        return true;
    }

    private sealed record PushCreateResult(JmapPushSubscriptionDB? Subscription, JsonObject? Error)
    {
        public static PushCreateResult Failed(string type) =>
            new(null, JmapMethodHelpers.SetError(type));
    }
}
