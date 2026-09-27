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
    private static readonly HashSet<string> Arguments = new HashSet<string>(
        ["ids", "properties"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> Properties = new HashSet<string>(
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
        var subscriptions = await database.JmapPushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserId == context.User.Id)
            .OrderBy(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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
        HashSet<string>? properties)
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
