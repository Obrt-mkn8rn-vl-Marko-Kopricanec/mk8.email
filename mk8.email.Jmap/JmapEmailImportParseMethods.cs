using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapEmailMutationHelpers
{
    public static async Task<(FolderDB? Folder, JsonObject? Error)> ResolveMailboxAsync(
        EmailDbContext database,
        Guid accountId,
        JsonNode? node,
        JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (node is not JsonObject map)
            return (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]));
        if (map.Count > 1)
            return (null, JmapMethodHelpers.SetError("tooManyMailboxes"));
        if (map.Count == 0)
            return (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]));
        var item = map.Single();
        if (item.Value is not JsonValue value
            || !value.TryGetValue<bool>(out var enabled)
            || !enabled
            || !JmapId.TryParseMailbox(context.ResolveId(item.Key), out var folderId))
        {
            return (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]));
        }
        var folder = await database.Folders
            .SingleOrDefaultAsync(candidate => candidate.Id == folderId
                && candidate.InboxId == accountId,
                cancellationToken);
        return folder is null
            ? (null, JmapMethodHelpers.SetError("invalidProperties", properties: ["mailboxIds"]))
            : (folder, null);
    }

    public static JsonObject CreatedEmail(EmailDB email) => new()
    {
        ["id"] = JmapId.Email(email.Id),
        ["blobId"] = JmapId.RawBlob(email.Id),
        ["threadId"] = JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
        ["size"] = email.SizeBytes,
    };

    public static bool TryGetObjectMap(
        JsonObject arguments,
        string name,
        bool required,
        out IReadOnlyDictionary<string, JsonObject>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
            return !required;
        if (node is not JsonObject map)
            return false;
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in map)
        {
            if (item.Value is not JsonObject value)
                return false;
            result[item.Key] = value;
        }
        values = result;
        return true;
    }

    public static DateTime DetermineReceivedAt(byte[] raw)
    {
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            foreach (var header in message.Headers.Where(header =>
                         header.Field.Equals("Received", StringComparison.OrdinalIgnoreCase)))
            {
                var separator = header.Value.LastIndexOf(';');
                if (separator >= 0
                    && MimeKit.Utils.DateUtils.TryParse(
                        header.Value[(separator + 1)..],
                        out var date))
                {
                    return date.UtcDateTime;
                }
            }
            return DateTime.UtcNow;
        }
        catch (FormatException)
        {
            return DateTime.UtcNow;
        }
    }
}

internal sealed class EmailImportMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    JmapBlobService blobs,
    JmapEmailStore store,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        ["blobId", "mailboxIds", "keywords", "receivedAt"],
        StringComparer.Ordinal);

    public string Name => "Email/import";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ifInState", "emails")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "emails", true, out var imports)
            || imports is null
            || !JmapMethodHelpers.AreValidCreationIds(imports.Keys))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (imports.Count > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var created = new JsonObject();
        var notCreated = new JsonObject();
        foreach (var item in imports)
        {
            if (!JmapId.IsValidId(item.Key)
                || item.Value.Any(property => !Properties.Contains(property.Key))
                || !JmapMethodHelpers.TryGetRequiredString(item.Value, "blobId", out var requestedBlobId))
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError("invalidProperties");
                continue;
            }
            var blobId = context.ResolveId(requestedBlobId);
            var blob = blobId is null
                ? null
                : await blobs.GetAsync(account.InboxId, blobId, cancellationToken);
            if (blob is null)
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError(
                    "invalidProperties",
                    properties: ["blobId"]);
                continue;
            }
            var mailbox = await JmapEmailMutationHelpers.ResolveMailboxAsync(
                database,
                account.InboxId,
                item.Value["mailboxIds"],
                context,
                cancellationToken);
            if (mailbox.Error is not null)
            {
                notCreated[item.Key] = mailbox.Error;
                continue;
            }
            string? keywordError = null;
            if (item.Value.ContainsKey("keywords") && item.Value["keywords"] is null
                || !JmapEmailStore.TryParseKeywords(
                    item.Value["keywords"],
                    out var keywords,
                    out keywordError))
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError(keywordError ?? "invalidProperties");
                continue;
            }
            var receivedAt = JmapEmailMutationHelpers.DetermineReceivedAt(blob.Content);
            if (item.Value.ContainsKey("receivedAt")
                && (item.Value["receivedAt"] is null
                    || !JmapEmailStore.TryParseReceivedAt(item.Value["receivedAt"], out receivedAt)))
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError(
                    "invalidProperties",
                    properties: ["receivedAt"]);
                continue;
            }
            var stored = await store.StoreAsync(
                account,
                mailbox.Folder!,
                blob.Content,
                keywords,
                receivedAt,
                cancellationToken);
            if (stored.Error is not null)
            {
                notCreated[item.Key] = stored.Error;
                continue;
            }
            var id = JmapId.Email(stored.Email!.Id);
            context.CreatedIds[item.Key] = id;
            created[item.Key] = JmapEmailMutationHelpers.CreatedEmail(stored.Email);
        }

        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.EmailDataType,
                cancellationToken),
            ["created"] = created.Count == 0 ? null : created,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
        });
    }
}

internal sealed class EmailParseMethod(
    JmapAccountService accounts,
    JmapBlobService blobs,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Email/parse";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "blobIds",
                "properties",
                "bodyProperties",
                "fetchTextBodyValues",
                "fetchHTMLBodyValues",
                "fetchAllBodyValues",
                "maxBodyValueBytes")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapEmailArguments.TryGetIds(arguments, "blobIds", false, out var blobIds)
            || blobIds is null
            || !JmapEmailArguments.TryGetProjectionOptions(
                arguments,
                JmapEmailCodec.ParseDefaultProperties,
                out var options))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (blobIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");

        var parsed = new JsonObject();
        var notParsable = new JsonArray();
        var notFound = new JsonArray();
        foreach (var blobId in blobIds.Distinct(StringComparer.Ordinal))
        {
            var blob = await blobs.GetAsync(account.InboxId, blobId, cancellationToken);
            if (blob is null)
            {
                notFound.Add(blobId);
                continue;
            }
            try
            {
                using var message = JmapEmailCodec.Parse(blob.Content);
                var source = BlobSource(blobId);
                parsed[blobId] = JmapEmailCodec.BuildEmail(
                    message,
                    options,
                    source.Id,
                    uploadedBlobId: blobId,
                    rawSize: blob.Content.LongLength,
                    blobPartPrefix: source.PartPrefix);
            }
            catch (FormatException)
            {
                notParsable.Add(blobId);
            }
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["parsed"] = parsed.Count == 0 ? null : parsed,
            ["notParsable"] = notParsable.Count == 0 ? null : notParsable,
            ["notFound"] = notFound.Count == 0 ? null : notFound,
        });
    }

    private static (Guid Id, string? PartPrefix) BlobSource(string blobId)
    {
        if (JmapId.TryParseUploadedBlob(blobId, out var id)
            || JmapId.TryParseRawBlob(blobId, out id))
        {
            return (id, null);
        }
        return JmapId.TryParseBodyPartBlob(blobId, out id, out var partId)
            ? (id, partId)
            : (Guid.CreateVersion7(), null);
    }
}
