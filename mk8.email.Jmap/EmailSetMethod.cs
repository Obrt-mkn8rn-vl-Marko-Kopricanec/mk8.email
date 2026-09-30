using mk8.email.Contracts.Messaging;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailMimeDraftBuilder builder,
    JmapEmailStore store,
    MailboxMessageContentService content) : IMailMessageMutationService
{
    private static readonly HashSet<string> MutableProperties = new HashSet<string>(
        ["mailboxIds", "keywords"],
        StringComparer.Ordinal);

    public async Task<MailMessageMutationResult> MutateAsync(
        MailMessageMutationCommand command,
        AuthenticatedMailUser user,
        JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailMessageMutationStatus.AccountNotFound, null, null, [], [], []);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null
            && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return new(MailMessageMutationStatus.StateMismatch, null, null, [], [], []);

        var created = new List<MailMessageCreateOutcome>(command.Creates.Count);
        var updated = new List<MailMessageUpdateOutcome>(command.Updates.Count);
        var destroyed = new List<MailMessageDestroyOutcome>(command.Destroys.Count);
        foreach (var item in command.Creates)
            created.Add(await CreateAsync(account, item, context, cancellationToken).ConfigureAwait(false));
        foreach (var item in command.Updates)
            updated.Add(await UpdateOneAsync(account.InboxId, item, context, cancellationToken)
                .ConfigureAwait(false));
        foreach (var item in command.Destroys)
            destroyed.Add(await DestroyOneAsync(account.InboxId, item, context, cancellationToken)
                .ConfigureAwait(false));
        var newState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailMessageMutationStatus.Ok, oldState, newState, created, updated, destroyed);
    }

    private async Task<MailMessageCreateOutcome> CreateAsync(
        JmapAccount account, MailMessageCreate item, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var value = item.Draft;
        if (!JmapId.IsValidId(item.CreationId))
            return new(item.CreationId, null, Failure("invalidProperties"));
        var mailbox = await ResolveDraftMailboxAsync(account.InboxId, value, context,
            cancellationToken).ConfigureAwait(false);
        if (mailbox.Error is not null)
            return new(item.CreationId, null, Failure(mailbox.Error));
        if (value.KeywordIssue != MailMessageKeywordIssue.None)
            return new(item.CreationId, null, Failure(value.KeywordIssue == MailMessageKeywordIssue.TooMany
                ? "tooManyKeywords" : "invalidProperties"));
        if (value.InvalidReceivedAt)
            return new(item.CreationId, null, Failure("invalidProperties", properties: ["receivedAt"]));
        var built = await builder.BuildAsync(account.InboxId, account.Address, context, value,
            cancellationToken).ConfigureAwait(false);
        if (built.Failure is not null)
            return new(item.CreationId, null, built.Failure);
        var stored = await store.StoreAsync(account, mailbox.Folder!, built.Raw!,
            value.Keywords.ToHashSet(StringComparer.Ordinal), value.ReceivedAt ?? DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (stored.Error is not null)
            return new(item.CreationId, null, Failure(stored.Error));
        var email = stored.Email!;
        context.CreatedIds[item.CreationId] = JmapId.Email(email.Id);
        return new(item.CreationId, new(email.Id,
            email.ThreadObjectId ?? email.Id.ToString("N"), email.SizeBytes), null);
    }

    private async Task<MailMessageUpdateOutcome> UpdateOneAsync(
        Guid accountId, MailMessageUpdate item, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var resolvedId = context.ResolveId(item.RequestedId);
        if (!JmapId.TryParseEmail(resolvedId, out var emailId))
            return new(item.RequestedId, null, Failure("notFound"));
        if (ApplicationValueCodec.Decode(item.Patch) is not JsonObject patch)
            throw new InvalidOperationException("The message patch is not an object.");
        var error = await UpdateAsync(accountId, emailId, context, patch, cancellationToken)
            .ConfigureAwait(false);
        return error is null
            ? new(item.RequestedId, emailId, null)
            : new(item.RequestedId, null, Failure(error));
    }

    private async Task<MailMessageDestroyOutcome> DestroyOneAsync(
        Guid accountId, MailMessageDestroy item, JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var resolvedId = context.ResolveId(item.RequestedId);
        if (!JmapId.TryParseEmail(resolvedId, out var emailId))
            return new(item.RequestedId, null, Failure("notFound"));
        var error = await DestroyAsync(accountId, emailId, cancellationToken).ConfigureAwait(false);
        return error is null
            ? new(item.RequestedId, emailId, null)
            : new(item.RequestedId, null, Failure(error));
    }

    private static MailMessageMutationFailure Failure(
        string type, string? description = null, IReadOnlyList<string>? properties = null) =>
        new(ParseError(type), description, properties, null);

    private static MailMessageMutationFailure Failure(JsonObject error)
    {
        var type = error["type"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The message operation returned an error without a kind.");
        return new(ParseError(type), error["description"]?.GetValue<string>(),
            ReadStrings(error["properties"]), ReadStrings(error["notFound"]));
    }

    private static MailMessageMutationError ParseError(string type) => type switch
    {
        "invalidProperties" => MailMessageMutationError.InvalidProperties,
        "invalidPatch" => MailMessageMutationError.InvalidPatch,
        "notFound" => MailMessageMutationError.NotFound,
        "tooManyMailboxes" => MailMessageMutationError.TooManyMailboxes,
        "blobNotFound" => MailMessageMutationError.BlobNotFound,
        "tooManyKeywords" => MailMessageMutationError.TooManyKeywords,
        "invalidEmail" => MailMessageMutationError.InvalidEmail,
        "tooLarge" => MailMessageMutationError.TooLarge,
        "overQuota" => MailMessageMutationError.OverQuota,
        _ => throw new InvalidOperationException("The message operation returned an unknown error kind."),
    };

    private static string[]? ReadStrings(JsonNode? value) =>
        value is JsonArray array ? array.Select(item => item!.GetValue<string>()).ToArray() : null;

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
                cancellationToken).ConfigureAwait(false);
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
                var rawMessage = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                using var message = JmapEmailCodec.Parse(rawMessage);
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
            cancellationToken).ConfigureAwait(false);
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
            await database.ExpungedUids.AddAsync(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = email.Uid,
                ModSeq = ++source.HighestModSeq,
                FolderId = source.Id,
            }, cancellationToken).ConfigureAwait(false);
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
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            var separator = item.Key.IndexOf('/', StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var root = item.Key[..separator];
            if (root is not ("bodyStructure" or "textBody" or "htmlBody" or "attachments"))
                continue;
            var remainder = item.Key[(separator + 1)..];
            var nextSeparator = remainder.IndexOf('/', StringComparison.Ordinal);
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
                if (string.Equals(item.Key, "subParts", StringComparison.Ordinal))
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
                cancellationToken).ConfigureAwait(false);
        if (email is null)
            return JmapMethodHelpers.SetError("notFound");
        await database.ExpungedUids.AddAsync(new ExpungedUidDB
        {
            Id = Guid.CreateVersion7(),
            Uid = email.Uid,
            ModSeq = ++email.Folder.HighestModSeq,
            FolderId = email.FolderId,
        }, cancellationToken).ConfigureAwait(false);
        content.DeleteOnCommit(email);
        database.Emails.Remove(email);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
        return folder is null
            ? MailboxResult.Failed("invalidProperties")
            : new MailboxResult(folder, null);
    }

    private async Task<MailboxResult> ResolveDraftMailboxAsync(Guid accountId, MailMessageDraft draft,
        JmapInvocationContext context, CancellationToken cancellationToken)
    {
        if (draft.FolderIssue != MailMessageMailboxIssue.None)
            return MailboxResult.Failed(draft.FolderIssue == MailMessageMailboxIssue.TooMany
                ? "tooManyMailboxes" : "invalidProperties");
        if (!JmapId.TryParseMailbox(context.ResolveId(draft.FolderReference!), out var folderId))
            return MailboxResult.Failed("invalidProperties");
        var folder = await database.Folders.FirstOrDefaultAsync(candidate => candidate.Id == folderId
            && candidate.InboxId == accountId, cancellationToken).ConfigureAwait(false);
        return folder is null ? MailboxResult.Failed("invalidProperties") : new(folder, null);
    }

    private sealed record MailboxResult(FolderDB? Folder, JsonObject? Error)
    {
        public static MailboxResult Failed(string type) =>
            new(null, JmapMethodHelpers.SetError(type));
    }
}
