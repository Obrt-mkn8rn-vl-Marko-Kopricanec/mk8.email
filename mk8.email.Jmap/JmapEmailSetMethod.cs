using System.Text.Json.Nodes;
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
            || !TryGetDestroy(arguments, out var destroy))
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
                if (!JmapEmailStore.TryParseKeywords(
                        item.Value["keywords"],
                        out var keywords,
                        out var keywordError))
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError(keywordError ?? "invalidProperties");
                    continue;
                }
                if (!JmapEmailStore.TryParseReceivedAt(item.Value["receivedAt"], out var receivedAt))
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
        var invalid = patch.KeysForPatch()
            .Where(property => !MutableProperties.Contains(property))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (invalid.Length > 0)
            return JmapMethodHelpers.SetError("invalidProperties", properties: invalid);
        var email = await database.Emails
            .Include(item => item.Folder)
            .SingleOrDefaultAsync(item => item.Id == emailId
                && item.Folder.InboxId == accountId
                && !item.IsDeleted,
                cancellationToken);
        if (email is null)
            return JmapMethodHelpers.SetError("notFound");

        var current = new JsonObject
        {
            ["mailboxIds"] = new JsonObject { [JmapId.Mailbox(email.FolderId)] = true },
            ["keywords"] = JmapEmailCodec.BuildKeywords(email),
        };
        if (!JmapMethodHelpers.TryApplyPatch(current, patch, out var result))
            return JmapMethodHelpers.SetError("invalidPatch");
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

        if (mailbox.Folder!.Id != email.FolderId)
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
