using mk8.email.Contracts.Messaging;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailImportMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    JmapBlobService blobs,
    JmapEmailStore store,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly HashSet<string> Properties = new HashSet<string>(
        ["blobId", "mailboxIds", "keywords", "receivedAt"],
        StringComparer.Ordinal);

    public MailOperationKind Operation => MailOperationKind.ImportMessages;
    public MailFeature Feature => MailFeature.Messages;

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
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
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
                : await blobs.GetAsync(account.InboxId, blobId, cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
            if (stored.Error is not null)
            {
                notCreated[item.Key] = stored.Error;
                continue;
            }
            var id = JmapId.Email(stored.Email!.Id);
            context.CreatedIds[item.Key] = id;
            created[item.Key] = JmapEmailMutationHelpers.CreatedEmail(stored.Email);
        }

        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.EmailDataType,
                cancellationToken).ConfigureAwait(false),
            ["created"] = created.Count == 0 ? null : created,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
        });
    }
}
