using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailCopyMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    JmapEmailStore store,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        ["id", "mailboxIds", "keywords", "receivedAt"],
        StringComparer.Ordinal);

    public string Name => "Email/copy";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "fromAccountId",
                "accountId",
                "ifFromInState",
                "ifInState",
                "create",
                "onSuccessDestroyOriginal",
                "destroyFromIfInState")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "fromAccountId", out var fromAccountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || string.Equals(fromAccountId, accountId, StringComparison.Ordinal)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifFromInState", out var ifFromInState)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "destroyFromIfInState", out var destroyFromIfInState)
            || !JmapMethodHelpers.TryGetOptionalBoolean(
                arguments,
                "onSuccessDestroyOriginal",
                false,
                out var destroyOriginal)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "create", true, out var create)
            || create is null
            || !JmapMethodHelpers.AreValidCreationIds(create.Keys))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (create.Count > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var sourceAccount = await accounts.GetAccountAsync(context.User, fromAccountId, cancellationToken);
        if (sourceAccount is null)
            return JmapMethodResponse.Error("fromAccountNotFound");
        var targetAccount = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (targetAccount is null)
            return JmapMethodResponse.Error("accountNotFound");

        var sourceState = await states.GetStateAsync(
            sourceAccount.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken);
        var oldTargetState = await states.GetStateAsync(
            targetAccount.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken);
        if (ifFromInState is not null && !string.Equals(ifFromInState, sourceState, StringComparison.Ordinal)
            || ifInState is not null && !string.Equals(ifInState, oldTargetState, StringComparison.Ordinal))
        {
            return JmapMethodResponse.Error("stateMismatch");
        }

        var created = new JsonObject();
        var notCreated = new JsonObject();
        var copiedSourceIds = new List<Guid>();
        foreach (var item in create)
        {
            if (!JmapId.IsValidId(item.Key)
                || item.Value.Any(property => !Properties.Contains(property.Key))
                || !JmapMethodHelpers.TryGetRequiredString(item.Value, "id", out var requestedSourceId)
                || !JmapId.TryParseEmail(context.ResolveId(requestedSourceId), out var sourceId))
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError("invalidProperties");
                continue;
            }
            var source = await database.Emails
                .AsNoTracking()
                .SingleOrDefaultAsync(email => email.Id == sourceId
                    && email.Folder.InboxId == sourceAccount.InboxId
                    && !email.IsDeleted,
                    cancellationToken);
            if (source is null)
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError("notFound");
                continue;
            }
            var mailbox = await JmapEmailMutationHelpers.ResolveMailboxAsync(
                database,
                targetAccount.InboxId,
                item.Value["mailboxIds"],
                context,
                cancellationToken);
            if (mailbox.Error is not null)
            {
                notCreated[item.Key] = mailbox.Error;
                continue;
            }
            IReadOnlySet<string> keywords;
            if (item.Value.ContainsKey("keywords"))
            {
                if (!JmapEmailStore.TryParseKeywords(item.Value["keywords"], out keywords, out var keywordError))
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError(keywordError ?? "invalidProperties");
                    continue;
                }
            }
            else
            {
                keywords = JmapEmailCodec.BuildKeywords(source)
                    .Select(property => property.Key)
                    .ToHashSet(StringComparer.Ordinal);
            }
            var receivedAt = source.ReceivedAt;
            if (item.Value.ContainsKey("receivedAt")
                && !JmapEmailStore.TryParseReceivedAt(item.Value["receivedAt"], out receivedAt))
            {
                notCreated[item.Key] = JmapMethodHelpers.SetError(
                    "invalidProperties",
                    properties: ["receivedAt"]);
                continue;
            }
            var stored = await store.StoreAsync(
                targetAccount,
                mailbox.Folder!,
                JmapEmailCodec.GetRawBytes(source),
                keywords,
                receivedAt,
                cancellationToken);
            if (stored.Error is not null)
            {
                notCreated[item.Key] = stored.Error;
                continue;
            }
            var newId = JmapId.Email(stored.Email!.Id);
            context.CreatedIds[item.Key] = newId;
            created[item.Key] = JmapEmailMutationHelpers.CreatedEmail(stored.Email);
            copiedSourceIds.Add(sourceId);
        }

        var copyResponse = new JsonObject
        {
            ["fromAccountId"] = fromAccountId,
            ["accountId"] = accountId,
            ["oldState"] = oldTargetState,
            ["newState"] = await states.GetStateAsync(
                targetAccount.InboxId,
                JmapConstants.EmailDataType,
                cancellationToken),
            ["created"] = created.Count == 0 ? null : created,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
        };
        if (!destroyOriginal || copiedSourceIds.Count == 0)
            return new JmapMethodResponse(Name, copyResponse);

        var currentSourceState = await states.GetStateAsync(
            sourceAccount.InboxId,
            JmapConstants.EmailDataType,
            cancellationToken);
        if (destroyFromIfInState is not null
            && !string.Equals(destroyFromIfInState, currentSourceState, StringComparison.Ordinal))
        {
            return new JmapMethodResponse(
                Name,
                copyResponse,
                [JmapMethodResponse.Error("stateMismatch")]);
        }

        var destroyed = new JsonArray();
        var notDestroyed = new JsonObject();
        foreach (var sourceId in copiedSourceIds.Distinct())
        {
            var source = await database.Emails
                .Include(email => email.Folder)
                .SingleOrDefaultAsync(email => email.Id == sourceId
                    && email.Folder.InboxId == sourceAccount.InboxId
                    && !email.IsDeleted,
                    cancellationToken);
            var wireId = JmapId.Email(sourceId);
            if (source is null)
            {
                notDestroyed[wireId] = JmapMethodHelpers.SetError("notFound");
                continue;
            }
            database.ExpungedUids.Add(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = source.Uid,
                ModSeq = ++source.Folder.HighestModSeq,
                FolderId = source.FolderId,
            });
            database.Emails.Remove(source);
            await database.SaveChangesAsync(cancellationToken);
            destroyed.Add(wireId);
        }
        var setResponse = new JmapMethodResponse("Email/set", new JsonObject
        {
            ["accountId"] = fromAccountId,
            ["oldState"] = currentSourceState,
            ["newState"] = await states.GetStateAsync(
                sourceAccount.InboxId,
                JmapConstants.EmailDataType,
                cancellationToken),
            ["created"] = null,
            ["updated"] = null,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = null,
            ["notUpdated"] = null,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
        return new JmapMethodResponse(Name, copyResponse, [setResponse]);
    }
}
