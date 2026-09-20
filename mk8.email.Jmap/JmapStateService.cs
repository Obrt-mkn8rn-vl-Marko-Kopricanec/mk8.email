using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

public sealed record JmapChangesResult(
    string OldState,
    string NewState,
    bool HasMoreChanges,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Destroyed);

public sealed class JmapStateService(
    EmailDbContext database,
    JmapAccountService accounts)
{
    private const string BaselineDataType = "_Account";

    public async Task<string> GetStateAsync(
        Guid accountId,
        string dataType,
        CancellationToken cancellationToken = default)
    {
        await EnsureBaselineAsync(accountId, cancellationToken);
        var sequence = await database.JmapChanges
            .AsNoTracking()
            .Where(change => change.AccountId == accountId && change.DataType == dataType)
            .MaxAsync(change => (long?)change.Sequence, cancellationToken)
            ?? 0;
        return FormatState(sequence);
    }

    public async Task<JmapChangesResult?> GetChangesAsync(
        Guid accountId,
        string dataType,
        string sinceState,
        long? maximumChanges,
        int serverMaximum,
        CancellationToken cancellationToken = default)
    {
        await EnsureBaselineAsync(accountId, cancellationToken);
        if (!TryParseState(sinceState, out var sinceSequence))
            return null;

        var currentSequence = await database.JmapChanges
            .AsNoTracking()
            .Where(change => change.AccountId == accountId && change.DataType == dataType)
            .MaxAsync(change => (long?)change.Sequence, cancellationToken)
            ?? 0;
        if (sinceSequence > currentSequence)
            return null;
        if (sinceSequence != 0
            && !await database.JmapChanges.AsNoTracking().AnyAsync(
                change => change.Sequence == sinceSequence
                    && change.AccountId == accountId
                    && change.DataType == dataType,
                cancellationToken))
        {
            return null;
        }

        var limit = JmapMethodHelpers.ClampToServerLimit(maximumChanges, serverMaximum);
        if (limit < 1)
            return null;

        var rows = database.JmapChanges
            .AsNoTracking()
            .Where(change => change.AccountId == accountId
                && change.DataType == dataType
                && change.Sequence > sinceSequence
                && change.Sequence <= currentSequence)
            .OrderBy(change => change.Sequence)
            .Select(change => new
            {
                change.Sequence,
                change.ObjectId,
                change.ChangeKind,
            })
            .AsAsyncEnumerable();

        var folded = new Dictionary<string, string>(StringComparer.Ordinal);
        var newSequence = sinceSequence;
        var hasMoreChanges = false;
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var hadExisting = folded.TryGetValue(row.ObjectId, out var existingKind);
            FoldChange(folded, row.ObjectId, row.ChangeKind);
            if (folded.Count > limit)
            {
                if (hadExisting)
                    folded[row.ObjectId] = existingKind!;
                else
                    folded.Remove(row.ObjectId);
                hasMoreChanges = true;
                break;
            }
            newSequence = row.Sequence;
        }

        if (!hasMoreChanges)
            hasMoreChanges = newSequence < currentSequence;
        if (!hasMoreChanges)
            newSequence = currentSequence;

        return new JmapChangesResult(
            sinceState,
            FormatState(newSequence),
            hasMoreChanges,
            folded.Where(item => item.Value == JmapConstants.CreatedChange).Select(item => item.Key).ToArray(),
            folded.Where(item => item.Value == JmapConstants.UpdatedChange).Select(item => item.Key).ToArray(),
            folded.Where(item => item.Value == JmapConstants.DestroyedChange).Select(item => item.Key).ToArray());
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> GetUserStatesAsync(
        mk8.email.Application.Interfaces.AuthenticatedMailUser user,
        IReadOnlySet<string>? requestedTypes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var account in await accounts.GetAccountsAsync(user, cancellationToken))
        {
            await EnsureBaselineAsync(account.InboxId, cancellationToken);
            var query = database.JmapChanges
                .AsNoTracking()
                .Where(change => change.AccountId == account.InboxId
                    && change.DataType != BaselineDataType);
            if (requestedTypes is not null)
            {
                var requested = requestedTypes.ToArray();
                query = query.Where(change => requested.Contains(change.DataType));
            }
            var states = await query
                .GroupBy(change => change.DataType)
                .Select(group => new
                {
                    DataType = group.Key,
                    Sequence = group.Max(change => change.Sequence),
                })
                .ToDictionaryAsync(
                    item => item.DataType,
                    item => FormatState(item.Sequence),
                    StringComparer.Ordinal,
                    cancellationToken);
            result[JmapId.Account(account.InboxId)] = states;
        }
        return result;
    }

    public static string FormatState(long sequence) => $"s{sequence}";

    public static bool TryParseState(string? state, out long sequence)
    {
        sequence = 0;
        return state is { Length: > 1 }
            && state[0] == 's'
            && long.TryParse(
                state.AsSpan(1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out sequence)
            && sequence >= 0;
    }

    private async Task EnsureBaselineAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (await database.JmapChanges.AsNoTracking().AnyAsync(
            change => change.AccountId == accountId && change.DataType == BaselineDataType,
            cancellationToken))
        {
            return;
        }

        var accountExists = await database.Inboxes.AsNoTracking().AnyAsync(
            inbox => inbox.Id == accountId && inbox.AliasForInboxId == null && inbox.Name != "*",
            cancellationToken);
        if (!accountExists)
            return;

        var now = DateTime.UtcNow;
        database.JmapChanges.Add(new JmapChangeDB
        {
            AccountId = accountId,
            DataType = BaselineDataType,
            ObjectId = JmapId.Account(accountId),
            ChangeKind = JmapConstants.CreatedChange,
            ChangedAt = now,
        });

        var mailboxIds = await database.Folders
            .AsNoTracking()
            .Where(folder => folder.InboxId == accountId)
            .Select(folder => folder.Id)
            .ToListAsync(cancellationToken);
        foreach (var mailboxId in mailboxIds)
        {
            AddBaselineChange(
                accountId,
                JmapConstants.MailboxDataType,
                JmapId.Mailbox(mailboxId),
                now);
        }

        var emails = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.InboxId == accountId && !email.IsDeleted)
            .Select(email => new
            {
                email.Id,
                email.ThreadObjectId,
            })
            .ToListAsync(cancellationToken);
        foreach (var email in emails)
            AddBaselineChange(accountId, JmapConstants.EmailDataType, JmapId.Email(email.Id), now);
        foreach (var threadId in emails
                     .Select(email => email.ThreadObjectId ?? email.Id.ToString("N"))
                     .Distinct(StringComparer.Ordinal))
        {
            AddBaselineChange(accountId, JmapConstants.ThreadDataType, JmapId.Thread(threadId), now);
        }

        AddBaselineChange(
            accountId,
            JmapConstants.IdentityDataType,
            JmapId.Identity(accountId),
            now);
        if (await database.JmapVacationResponses.AsNoTracking().AnyAsync(
            response => response.AccountId == accountId,
            cancellationToken))
        {
            AddBaselineChange(
                accountId,
                JmapConstants.VacationResponseDataType,
                "singleton",
                now);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var baselineExists = await database.JmapChanges
                .AsNoTracking()
                .AnyAsync(
                    change => change.AccountId == accountId
                        && change.DataType == BaselineDataType,
                    cancellationToken);
            if (!baselineExists)
                throw;

            foreach (var entry in database.ChangeTracker.Entries<JmapChangeDB>()
                         .Where(entry => entry.State == EntityState.Added
                             && entry.Entity.AccountId == accountId)
                         .ToArray())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private void AddBaselineChange(
        Guid accountId,
        string dataType,
        string objectId,
        DateTime changedAt)
    {
        database.JmapChanges.Add(new JmapChangeDB
        {
            AccountId = accountId,
            DataType = dataType,
            ObjectId = objectId,
            ChangeKind = JmapConstants.CreatedChange,
            ChangedAt = changedAt,
        });
    }

    private static void FoldChange(
        IDictionary<string, string> changes,
        string objectId,
        string kind)
    {
        if (!changes.TryGetValue(objectId, out var existing))
        {
            changes[objectId] = kind;
            return;
        }

        var folded = (existing, kind) switch
        {
            (JmapConstants.CreatedChange, JmapConstants.UpdatedChange) => JmapConstants.CreatedChange,
            (JmapConstants.CreatedChange, JmapConstants.DestroyedChange) => null,
            (JmapConstants.UpdatedChange, JmapConstants.DestroyedChange) => JmapConstants.DestroyedChange,
            (JmapConstants.DestroyedChange, JmapConstants.CreatedChange) => JmapConstants.UpdatedChange,
            (_, JmapConstants.CreatedChange) => JmapConstants.CreatedChange,
            (_, JmapConstants.DestroyedChange) => JmapConstants.DestroyedChange,
            _ => existing,
        };
        if (folded is null)
            changes.Remove(objectId);
        else
            changes[objectId] = folded;
    }
}
