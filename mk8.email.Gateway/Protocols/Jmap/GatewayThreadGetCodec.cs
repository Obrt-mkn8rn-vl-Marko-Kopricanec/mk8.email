using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayThreadGetCodec
{
    private static readonly HashSet<string> AllowedProperties = new(["id", "emailIds"], StringComparer.Ordinal);

    internal sealed record Call(
        MailThreadReadCommand Command,
        string AccountId,
        IReadOnlyList<string>? RequestedIds,
        IReadOnlySet<string>? Properties,
        int MaximumObjects);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "ids" or "properties"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || !TryParseProperties(arguments, out var properties)
            || !TryParseIds(arguments, out var requestedIds))
        {
            failure = "invalidArguments";
            return false;
        }
        if (requestedIds?.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        if (accountId.Length != 33 || accountId[0] != 'A'
            || !Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }
        call = new(new(accountGuid), accountId, requestedIds, properties, maximumObjects);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailThreadReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Emails is null)
            throw new InvalidOperationException("The Application returned an invalid thread read result.");
        if (result.Status == MailThreadReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.State is null)
            throw new InvalidOperationException("The Application returned an incomplete thread read result.");

        var threads = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var email in result.Emails)
        {
            if (email is null || email.EmailId == Guid.Empty)
                throw new InvalidOperationException("The Application returned invalid thread email data.");
            var id = FormatThreadId(email.StoredThreadId ?? email.EmailId.ToString("N"));
            if (!threads.TryGetValue(id, out var emailIds))
            {
                emailIds = [];
                threads.Add(id, emailIds);
            }
            emailIds.Add($"E{email.EmailId:N}");
        }
        if (call.RequestedIds is null && threads.Count > call.MaximumObjects)
            return Error("requestTooLarge");

        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? threads.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (!threads.TryGetValue(id, out var emailIds))
            {
                notFound.Add(id);
                continue;
            }
            var thread = new JsonObject { ["id"] = id };
            if (call.Properties is null || call.Properties.Contains("emailIds"))
            {
                var values = new JsonArray();
                for (var index = 0; index < emailIds.Count; index++)
                    values.Add(emailIds[index]);
                thread["emailIds"] = values;
            }
            list.Add(thread);
        }
        return (MailOperationKind.ReadThreads, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static string FormatThreadId(string storedThreadId)
    {
        if (storedThreadId.Length is > 0 and < 255
            && storedThreadId.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_'))
            return "T" + storedThreadId;
        return "T" + Convert.ToBase64String(Encoding.UTF8.GetBytes(storedThreadId))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryParseProperties(JsonObject arguments, out FrozenSet<string>? properties)
    {
        properties = null;
        if (!arguments.TryGetPropertyValue("properties", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name)
                || name is null || !AllowedProperties.Contains(name))
                return false;
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static bool TryParseIds(JsonObject arguments, out string[]? ids)
    {
        ids = null;
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var requested = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            requested.Add(id);
        }
        ids = requested.ToArray();
        return true;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
