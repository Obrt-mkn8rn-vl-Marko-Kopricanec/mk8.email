using System.Text.Json.Nodes;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    JmapEmailBuilder builder,
    JmapEmailStore store,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> MutableProperties = new HashSet<string>(
        ["mailboxIds", "keywords"],
        StringComparer.Ordinal);

    public string Name => "Email/set";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "ifInState",
                "create",
                "update",
                "destroy")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !TryGetObjectMap(arguments, "create", out var create)
            || !TryGetObjectMap(arguments, "update", out var update)
            || !TryGetDestroy(arguments, out var destroy)
            || !JmapMethodHelpers.AreValidCreationIds(create?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(update?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(destroy))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var operationCount = (create?.Count ?? 0) + (update?.Count ?? 0) + (destroy?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
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
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();

        if (create is not null)
        {
            foreach (var item in create)
            {
                if (!JmapId.IsValidId(item.Key))
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError("invalidProperties");
                    continue;
                }
                var mailbox = await ResolveMailboxAsync(
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
                if (item.Value.ContainsKey("receivedAt") && item.Value["receivedAt"] is null
                    || !JmapEmailStore.TryParseReceivedAt(item.Value["receivedAt"], out var receivedAt))
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: ["receivedAt"]);
                    continue;
                }

                var built = await builder.BuildAsync(
                    account.InboxId,
                    account.Address,
                    context,
                    item.Value,
                    cancellationToken);
                if (built.Error is not null)
                {
                    notCreated[item.Key] = built.Error;
                    continue;
                }
                using var message = built.Value!.Message;
                var stored = await store.StoreAsync(
                    account,
                    mailbox.Folder!,
                    built.Value.RawBytes,
                    keywords,
                    receivedAt,
                    cancellationToken);
                if (stored.Error is not null)
                {
                    notCreated[item.Key] = stored.Error;
                    continue;
                }
                var email = stored.Email!;
                var id = JmapId.Email(email.Id);
                context.CreatedIds[item.Key] = id;
                created[item.Key] = CreatedEmail(email);
            }
        }

        if (update is not null)
        {
            foreach (var item in update)
            {
                var resolvedId = context.ResolveId(item.Key);
                if (!JmapId.TryParseEmail(resolvedId, out var emailId))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var error = await UpdateAsync(
                    account.InboxId,
                    emailId,
                    context,
                    item.Value,
                    cancellationToken);
                if (error is null)
                    updated[resolvedId!] = null;
                else
                    notUpdated[item.Key] = error;
            }
        }

        if (destroy is not null)
        {
            foreach (var requestedId in destroy.Distinct(StringComparer.Ordinal))
            {
                var resolvedId = context.ResolveId(requestedId);
                if (!JmapId.TryParseEmail(resolvedId, out var emailId))
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var error = await DestroyAsync(account.InboxId, emailId, cancellationToken);
                if (error is null)
                    destroyed.Add(resolvedId);
                else
                    notDestroyed[requestedId] = error;
            }
        }

        var newState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken);
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = newState,
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private async Task<JsonObject?> UpdateAsync(
        Guid accountId,
        Guid emailId,
        JmapInvocationContext context,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        var email = await database.Emails
            .Include(item => item.Folder)
            .SingleOrDefaultAsync(item => item.Id == emailId
                && item.Folder.InboxId == accountId
                && !item.IsDeleted,
                cancellationToken);
        if (email is null)
            return JmapMethodHelpers.SetError("notFound");

        JsonObject current;
        var includesImmutableProperties = patch.KeysForPatch()
            .Any(property => !MutableProperties.Contains(property));
        if (!includesImmutableProperties)
        {
            current = new JsonObject
            {
                ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(email.FolderId)] = true },
                ["keywords"] = JmapEmailCodec.BuildKeywords(email),
            };
        }
        else
        {
            try
            {
                using var message = JmapEmailCodec.Parse(email);
                if (!TryBuildUpdateSource(
                        message,
                        email,
                        patch,
                        out current,
                        out var projectionError))
                {
                    return projectionError;
                }
            }
            catch (FormatException)
            {
                return JmapMethodHelpers.SetError(
                    "invalidProperties",
                    "The immutable MIME representation could not be verified.");
            }
        }
        if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                current,
                patch,
                MutableProperties,
                out var result,
                out var invalidProperties))
        {
            return JmapMethodHelpers.SetError("invalidPatch");
        }
        if (invalidProperties.Count > 0)
            return JmapMethodHelpers.SetError("invalidProperties", properties: invalidProperties);
        var mailbox = await ResolveMailboxAsync(
            accountId,
            result["mailboxIds"],
            context,
            cancellationToken);
        if (mailbox.Error is not null)
            return mailbox.Error;
        if (!JmapEmailStore.TryParseKeywords(
                result["keywords"],
                out var keywords,
                out var keywordError))
        {
            return JmapMethodHelpers.SetError(keywordError ?? "invalidProperties");
        }

        var currentKeywords = JmapEmailCodec.BuildKeywords(email)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (mailbox.Folder!.Id == email.FolderId && currentKeywords.SetEquals(keywords))
            return null;

        if (mailbox.Folder.Id != email.FolderId)
        {
            var source = email.Folder;
            database.ExpungedUids.Add(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = email.Uid,
                ModSeq = ++source.HighestModSeq,
                FolderId = source.Id,
            });
            email.FolderId = mailbox.Folder.Id;
            email.Folder = mailbox.Folder;
            email.Uid = mailbox.Folder.NextUid++;
            email.ModSeq = ++mailbox.Folder.HighestModSeq;
        }
        else
        {
            email.ModSeq = ++email.Folder.HighestModSeq;
        }
        JmapEmailStore.ApplyKeywords(email, keywords);
        await database.SaveChangesAsync(cancellationToken);
        return null;
    }

    private static bool TryBuildUpdateSource(
        MimeKit.MimeMessage message,
        EmailDB email,
        JsonObject patch,
        out JsonObject source,
        out JsonObject? error)
    {
        source = null!;
        error = null;
        var properties = patch.KeysForPatch()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var invalidProperties = properties
            .Where(property => !JmapEmailCodec.IsValidProperty(property))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (invalidProperties.Length > 0)
        {
            error = JmapMethodHelpers.SetError(
                "invalidProperties",
                properties: invalidProperties);
            return false;
        }
        if (!properties.Contains("mailboxIds", StringComparer.Ordinal))
            properties.Add("mailboxIds");
        if (!properties.Contains("keywords", StringComparer.Ordinal))
            properties.Add("keywords");

        if (!TryInferBodyProperties(patch, out var bodyProperties, out var invalidBodyProperty))
        {
            error = JmapMethodHelpers.SetError(
                "invalidProperties",
                properties: [invalidBodyProperty!]);
            return false;
        }
        var options = new JmapEmailProjectionOptions(
            properties,
            bodyProperties,
            FetchTextBodyValues: false,
            FetchHtmlBodyValues: false,
            FetchAllBodyValues: true,
            MaxBodyValueBytes: 0);
        source = JmapEmailCodec.BuildEmail(message, options, email.Id, email);

        if (patch.TryGetPropertyValue("bodyValues", out var requestedBodyValues))
        {
            if (!TryBuildBodyValueProjection(
                    message,
                    email,
                    requestedBodyValues,
                    out var projectedBodyValues))
            {
                error = JmapMethodHelpers.SetError(
                    "invalidProperties",
                    properties: ["bodyValues"]);
                return false;
            }
            source["bodyValues"] = projectedBodyValues;
        }
        return true;
    }

    private static bool TryInferBodyProperties(
        JsonObject patch,
        out IReadOnlyList<string> properties,
        out string? invalidProperty)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var sawExplicitNullSubParts = false;
        foreach (var property in new[] { "bodyStructure", "textBody", "htmlBody", "attachments" })
        {
            if (patch.TryGetPropertyValue(property, out var node))
                Collect(node);
        }
        foreach (var item in patch)
        {
            var separator = item.Key.IndexOf('/');
            if (separator < 0)
                continue;
            var root = item.Key[..separator];
            if (root is not ("bodyStructure" or "textBody" or "htmlBody" or "attachments"))
                continue;
            var remainder = item.Key[(separator + 1)..];
            var nextSeparator = remainder.IndexOf('/');
            var token = nextSeparator < 0 ? remainder : remainder[..nextSeparator];
            result.Add(token.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal));
        }
        if (!sawExplicitNullSubParts)
            result.Remove("subParts");
        invalidProperty = result.FirstOrDefault(property => !JmapEmailCodec.IsValidBodyProperty(property));
        properties = result.ToArray();
        return invalidProperty is null;

        void Collect(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (var child in array)
                    Collect(child);
                return;
            }
            if (node is not JsonObject bodyPart)
                return;
            foreach (var item in bodyPart)
            {
                result.Add(item.Key);
                if (item.Key == "subParts")
                {
                    if (item.Value is null)
                        sawExplicitNullSubParts = true;
                    else
                        Collect(item.Value);
                }
            }
        }
    }

    private static bool TryBuildBodyValueProjection(
        MimeKit.MimeMessage message,
        EmailDB email,
        JsonNode? requestedNode,
        out JsonObject projection)
    {
        projection = new JsonObject();
        if (requestedNode is not JsonObject requested)
            return false;

        JsonObject? complete = null;
        foreach (var flags in BodyValueFlagCombinations)
        {
            var options = new JmapEmailProjectionOptions(
                ["bodyValues"],
                [],
                flags.FetchText,
                flags.FetchHtml,
                flags.FetchAll,
                0);
            var candidate = JmapEmailCodec.BuildEmail(message, options, email.Id, email)["bodyValues"]!
                .AsObject();
            if (candidate.Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(requested.Select(item => item.Key)))
            {
                complete = candidate;
                break;
            }
        }
        if (complete is null)
            return false;

        var minimumLimit = 1;
        var maximumLimit = int.MaxValue;
        var sawTruncation = false;
        foreach (var item in requested)
        {
            if (item.Value is not JsonObject requestedValue
                || requestedValue.Count != 3
                || requestedValue["value"] is not JsonValue requestedTextNode
                || !requestedTextNode.TryGetValue<string>(out var requestedText)
                || requestedValue["isEncodingProblem"] is not JsonValue requestedProblemNode
                || !requestedProblemNode.TryGetValue<bool>(out var requestedProblem)
                || requestedValue["isTruncated"] is not JsonValue requestedTruncatedNode
                || !requestedTruncatedNode.TryGetValue<bool>(out var requestedTruncated)
                || complete[item.Key] is not JsonObject completeValue
                || completeValue["value"] is not JsonValue completeTextNode
                || !completeTextNode.TryGetValue<string>(out var completeText)
                || completeValue["isEncodingProblem"] is not JsonValue completeProblemNode
                || !completeProblemNode.TryGetValue<bool>(out var completeProblem)
                || requestedProblem != completeProblem)
            {
                return false;
            }

            if (!requestedTruncated)
            {
                if (!string.Equals(requestedText, completeText, StringComparison.Ordinal))
                    return false;
                minimumLimit = Math.Max(minimumLimit, Encoding.UTF8.GetByteCount(completeText));
                continue;
            }
            if (string.Equals(requestedText, completeText, StringComparison.Ordinal)
                || !completeText.StartsWith(requestedText, StringComparison.Ordinal))
            {
                return false;
            }
            var nextRune = Rune.GetRuneAt(completeText, requestedText.Length);
            var prefixBytes = Encoding.UTF8.GetByteCount(requestedText);
            sawTruncation = true;
            minimumLimit = Math.Max(minimumLimit, prefixBytes);
            maximumLimit = Math.Min(maximumLimit, prefixBytes + nextRune.Utf8SequenceLength - 1);
        }
        if (sawTruncation && minimumLimit > maximumLimit)
            return false;

        projection = (JsonObject)requested.DeepClone();
        return true;
    }

    private static readonly (bool FetchText, bool FetchHtml, bool FetchAll)[] BodyValueFlagCombinations =
    [
        (false, false, false),
        (true, false, false),
        (false, true, false),
        (true, true, false),
        (false, false, true),
    ];

    private async Task<JsonObject?> DestroyAsync(
        Guid accountId,
        Guid emailId,
        CancellationToken cancellationToken)
    {
        var email = await database.Emails
            .Include(item => item.Folder)
            .SingleOrDefaultAsync(item => item.Id == emailId
                && item.Folder.InboxId == accountId
                && !item.IsDeleted,
                cancellationToken);
        if (email is null)
            return JmapMethodHelpers.SetError("notFound");
        database.ExpungedUids.Add(new ExpungedUidDB
        {
            Id = Guid.CreateVersion7(),
            Uid = email.Uid,
            ModSeq = ++email.Folder.HighestModSeq,
            FolderId = email.FolderId,
        });
        database.Emails.Remove(email);
        await database.SaveChangesAsync(cancellationToken);
        return null;
    }

    private async Task<MailboxResult> ResolveMailboxAsync(
        Guid accountId,
        JsonNode? node,
        JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (node is not JsonObject map)
            return MailboxResult.Failed("invalidProperties");
        if (map.Count > 1)
            return MailboxResult.Failed("tooManyMailboxes");
        if (map.Count == 0)
            return MailboxResult.Failed("invalidProperties");
        var item = map.Single();
        if (item.Value is not JsonValue value
            || !value.TryGetValue<bool>(out var enabled)
            || !enabled)
        {
            return MailboxResult.Failed("invalidProperties");
        }
        var resolvedId = context.ResolveId(item.Key);
        if (!JmapId.TryParseMailbox(resolvedId, out var folderId))
            return MailboxResult.Failed("invalidProperties");
        var folder = await database.Folders
            .SingleOrDefaultAsync(candidate => candidate.Id == folderId
                && candidate.InboxId == accountId,
                cancellationToken);
        return folder is null
            ? MailboxResult.Failed("invalidProperties")
            : new MailboxResult(folder, null);
    }

    private static JsonObject CreatedEmail(EmailDB email) => new()
    {
        ["id"] = JmapId.Email(email.Id),
        ["blobId"] = JmapId.RawBlob(email.Id),
        ["threadId"] = JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
        ["size"] = email.SizeBytes,
    };

    private static bool TryGetObjectMap(
        JsonObject arguments,
        string name,
        out IReadOnlyDictionary<string, JsonObject>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
            return true;
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

    private static bool TryGetDestroy(
        JsonObject arguments,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var id)
                || id is null)
            {
                return false;
            }
            result.Add(id);
        }
        values = result;
        return true;
    }

    private sealed record MailboxResult(FolderDB? Folder, JsonObject? Error)
    {
        public static MailboxResult Failed(string type) =>
            new(null, JmapMethodHelpers.SetError(type));
    }
}
