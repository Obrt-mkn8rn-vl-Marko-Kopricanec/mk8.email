using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed record JmapStateChangePoll(
    long Cursor,
    JsonObject? StateChange);

internal sealed class JmapStateChangeService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states)
{
    public static readonly IReadOnlySet<string> SupportedTypes = new HashSet<string>(
        [
            JmapConstants.MailboxDataType,
            JmapConstants.ThreadDataType,
            JmapConstants.EmailDataType,
            JmapConstants.EmailDeliveryDataType,
            JmapConstants.IdentityDataType,
            JmapConstants.EmailSubmissionDataType,
            JmapConstants.VacationResponseDataType,
        ],
        StringComparer.Ordinal);

    public async Task<long> GetCursorAsync(
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        _ = await states.GetUserStatesAsync(user, null, cancellationToken);
        var accountIds = (await accounts.GetAccountsAsync(user, cancellationToken))
            .Select(account => account.InboxId)
            .ToArray();
        if (accountIds.Length == 0)
            return 0;
        return await database.JmapChanges
            .AsNoTracking()
            .Where(change => accountIds.Contains(change.AccountId))
            .MaxAsync(change => (long?)change.Sequence, cancellationToken)
            ?? 0;
    }

    public async Task<JmapStateChangePoll> PollAsync(
        AuthenticatedMailUser user,
        long afterCursor,
        IReadOnlySet<string>? requestedTypes,
        CancellationToken cancellationToken)
    {
        _ = await states.GetUserStatesAsync(user, null, cancellationToken);
        var accountIds = (await accounts.GetAccountsAsync(user, cancellationToken))
            .Select(account => account.InboxId)
            .ToArray();
        if (accountIds.Length == 0)
            return new JmapStateChangePoll(0, null);

        var currentCursor = await database.JmapChanges
            .AsNoTracking()
            .Where(change => accountIds.Contains(change.AccountId))
            .MaxAsync(change => (long?)change.Sequence, cancellationToken)
            ?? 0;
        if (currentCursor == afterCursor)
            return new JmapStateChangePoll(currentCursor, null);

        var effectiveAfter = afterCursor < 0 || afterCursor > currentCursor ? 0 : afterCursor;
        var changedKeys = await database.JmapChanges
            .AsNoTracking()
            .Where(change => accountIds.Contains(change.AccountId)
                && change.Sequence > effectiveAfter
                && change.Sequence <= currentCursor)
            .Select(change => new { change.AccountId, change.DataType })
            .Distinct()
            .ToListAsync(cancellationToken);
        changedKeys = changedKeys
            .Where(key => key.DataType != "_Account"
                && (requestedTypes is null || requestedTypes.Contains(key.DataType)))
            .ToList();
        if (changedKeys.Count == 0)
            return new JmapStateChangePoll(currentCursor, null);

        var changed = new JsonObject();
        foreach (var group in changedKeys.GroupBy(key => key.AccountId))
        {
            var typeStates = new JsonObject();
            foreach (var dataType in group.Select(key => key.DataType).Distinct(StringComparer.Ordinal))
            {
                typeStates[dataType] = await states.GetStateAsync(
                    group.Key,
                    dataType,
                    cancellationToken);
            }
            changed[JmapId.Account(group.Key)] = typeStates;
        }
        return new JmapStateChangePoll(currentCursor, new JsonObject
        {
            ["@type"] = "StateChange",
            ["changed"] = changed,
        });
    }
}
