using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailImportCodec
{
    internal sealed record Call(MailImportCommand Command, string AccountId);

    public static bool TryParse(JsonObject arguments, IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ifInState" or "emails"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryOptionalState(arguments, "ifInState", out var ifInState)
            || arguments["emails"] is not JsonObject imports
            || imports.Any(item => !GatewayJmapBatchCodec.IsId(item.Key) || item.Value is not JsonObject))
        {
            failure = "invalidArguments";
            return false;
        }
        if (imports.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        var items = imports.Select(item => ParseItem(item.Key, (JsonObject)item.Value!, knownEntities)).ToArray();
        var accountGuid = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var account)
                ? account : Guid.Empty;
        call = new(new(accountGuid, ifInState, items), accountId);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailImportResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Items is null)
            throw new InvalidOperationException("The Application returned an invalid import result.");
        if (result.Status == MailImportStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailImportStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status == MailImportStatus.RequestTooLarge) return Error("requestTooLarge");
        if (result.Status != MailImportStatus.Ok || result.OldState is null || result.NewState is null
            || result.Items.Count != call.Command.Items.Count)
            throw new InvalidOperationException("The Application returned an incomplete import result.");
        var created = new JsonObject();
        var notCreated = new JsonObject();
        for (var index = 0; index < result.Items.Count; index++)
        {
            var outcome = result.Items[index];
            var expected = call.Command.Items[index];
            if (outcome is null || !string.Equals(outcome.CreationId, expected.CreationId, StringComparison.Ordinal)
                || !Enum.IsDefined(outcome.Error))
                throw new InvalidOperationException("The Application returned inconsistent import items.");
            if (outcome.Error != MailImportItemError.None)
            {
                if (outcome.EmailId is not null || outcome.StoredThreadId is not null || outcome.Size is not null)
                    throw new InvalidOperationException("The Application returned an inconsistent import failure.");
                notCreated[outcome.CreationId] = ItemError(outcome.Error);
                continue;
            }
            if (outcome.EmailId is null || outcome.EmailId == Guid.Empty
                || string.IsNullOrEmpty(outcome.StoredThreadId) || outcome.Size is null or < 0)
                throw new InvalidOperationException("The Application returned an incomplete imported message.");
            var id = outcome.EmailId.Value;
            created[outcome.CreationId] = new JsonObject
            {
                ["id"] = $"E{id:N}",
                ["blobId"] = $"B{id:N}",
                ["threadId"] = FormatThreadId(outcome.StoredThreadId),
                ["size"] = outcome.Size.Value,
            };
        }
        return (MailOperationKind.ImportMessages, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldState"] = result.OldState,
            ["newState"] = result.NewState,
            ["created"] = created.Count == 0 ? null : created,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
        });
    }

    private static MailImportItem ParseItem(string creationId, JsonObject value,
        IReadOnlyDictionary<string, string> knownEntities)
    {
        var invalidInitial = value.Any(item => item.Key is not ("blobId" or "mailboxIds" or "keywords" or "receivedAt"))
            || value["blobId"] is not JsonValue blobValue
            || !blobValue.TryGetValue<string>(out _);
        var blobId = value["blobId"] is JsonValue blobNode && blobNode.TryGetValue<string>(out var rawBlob)
            ? Resolve(rawBlob, knownEntities) : null;
        var (mailboxId, mailboxIssue) = ParseMailbox(value["mailboxIds"], knownEntities);
        var (keywords, keywordIssue) = ParseKeywords(value);
        var receivedAt = ParseReceivedAt(value, out var invalidReceivedAt);
        return new(creationId, blobId, invalidInitial, mailboxId, mailboxIssue,
            keywords, keywordIssue, receivedAt, invalidReceivedAt);
    }

    internal static (Guid? Id, MailMessageMailboxIssue Issue) ParseMailbox(JsonNode? node,
        IReadOnlyDictionary<string, string> knownEntities)
    {
        if (node is not JsonObject map || map.Count == 0)
            return (null, MailMessageMailboxIssue.Invalid);
        if (map.Count > 1)
            return (null, MailMessageMailboxIssue.TooMany);
        var item = map.Single();
        var resolved = Resolve(item.Key, knownEntities);
        if (item.Value is not JsonValue value || !value.TryGetValue<bool>(out var enabled)
            || !enabled || resolved is not { Length: 33 } || resolved[0] != 'M'
            || !Guid.TryParseExact(resolved.AsSpan(1), "N", out var folderId))
            return (null, MailMessageMailboxIssue.Invalid);
        return (folderId, MailMessageMailboxIssue.None);
    }

    internal static (IReadOnlyList<string> Values, MailMessageKeywordIssue Issue) ParseKeywords(JsonObject item)
    {
        if (!item.TryGetPropertyValue("keywords", out var node))
            return ([], MailMessageKeywordIssue.None);
        if (node is not JsonObject map)
            return ([], MailMessageKeywordIssue.Invalid);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in map)
        {
            var keyword = CultureInfo.InvariantCulture.TextInfo.ToLower(property.Key);
            if (property.Value is not JsonValue value || !value.TryGetValue<bool>(out var enabled)
                || !enabled || !IsValidKeyword(keyword))
                return ([], MailMessageKeywordIssue.Invalid);
            result.Add(keyword);
        }
        return result.Count > 128
            ? ([], MailMessageKeywordIssue.TooMany)
            : (result.ToArray(), MailMessageKeywordIssue.None);
    }

    internal static DateTime? ParseReceivedAt(JsonObject item, out bool invalid)
    {
        invalid = false;
        if (!item.TryGetPropertyValue("receivedAt", out var node)) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)
            && GatewayJmapDateCodec.TryParseUtc(text, out var date)) return date;
        invalid = true;
        return null;
    }

    internal static bool TryOptionalState(JsonObject arguments, string name, out string? state)
    {
        state = null;
        return !arguments.TryGetPropertyValue(name, out var node) || node is null
            || node is JsonValue value && value.TryGetValue(out state);
    }

    private static bool IsValidKeyword(string keyword) =>
        keyword.Length is >= 1 and <= 255
        && !string.Equals(keyword, "$recent", StringComparison.OrdinalIgnoreCase)
        && keyword.All(character => character is >= (char)0x21 and <= (char)0x7e
            && character is not ('(' or ')' or '{' or ']' or '%' or '*' or '"' or '\\'));

    internal static string? Resolve(string value, IReadOnlyDictionary<string, string> knownEntities) =>
        value.Length > 0 && value[0] == '#'
            ? knownEntities.GetValueOrDefault(value[1..]) : value;

    internal static string FormatThreadId(string stored)
    {
        if (stored.Length is > 0 and < 255 && stored.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_')) return "T" + stored;
        return "T" + Convert.ToBase64String(Encoding.UTF8.GetBytes(stored))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static JsonObject ItemError(MailImportItemError error)
    {
        var (type, properties) = error switch
        {
            MailImportItemError.InvalidProperties => ("invalidProperties", Array.Empty<string>()),
            MailImportItemError.MissingBlob => ("invalidProperties", ["blobId"]),
            MailImportItemError.InvalidMailbox => ("invalidProperties", ["mailboxIds"]),
            MailImportItemError.TooManyMailboxes => ("tooManyMailboxes", Array.Empty<string>()),
            MailImportItemError.InvalidKeywords => ("invalidProperties", Array.Empty<string>()),
            MailImportItemError.TooManyKeywords => ("tooManyKeywords", Array.Empty<string>()),
            MailImportItemError.InvalidReceivedAt => ("invalidProperties", ["receivedAt"]),
            MailImportItemError.TooLarge => ("tooLarge", Array.Empty<string>()),
            MailImportItemError.OverQuota => ("overQuota", Array.Empty<string>()),
            MailImportItemError.InvalidEmail => ("invalidEmail", Array.Empty<string>()),
            _ => throw new InvalidOperationException("The Application returned an invalid import error."),
        };
        var response = new JsonObject { ["type"] = type };
        if (properties.Length > 0)
            response["properties"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property)).ToArray());
        return response;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
