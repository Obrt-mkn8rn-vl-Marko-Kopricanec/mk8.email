using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailContactMutationService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IMailContactMutationService
{
    public async Task<MailContactMutationResult> MutateAsync(
        MailContactMutationCommand command, AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = command.AccountReferenceParseable
            ? await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
                .ConfigureAwait(false) : null;
        if (account is null) return Error(MailContactMutationStatus.AccountNotFound);
        var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        if (!command.AccountReferenceEligible || available.Count == 0
            || available[0].InboxId != account.InboxId)
            return Error(MailContactMutationStatus.AccountNotSupported);
        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken).ConfigureAwait(false);
        await DavContactUidInvariant.AcquireAccountLockAsync(database, account.UserId,
            cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(account.InboxId, JmapConstants.ContactCardDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null
            && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return Error(MailContactMutationStatus.StateMismatch);
        var books = await contacts.LoadAddressBooksAsync(account.UserId, true, cancellationToken)
            .ConfigureAwait(false);
        var cards = await contacts.LoadCardsAsync(account.UserId, true, cancellationToken)
            .ConfigureAwait(false);
        var state = new MutationState(account.InboxId, books, cards, command.AddressBookAliases,
            command.Destroys);
        var created = new List<MailContactCreateOutcome>(command.Creates.Count);
        foreach (var item in command.Creates)
            created.Add(await CreateOneAsync(state, item, cancellationToken).ConfigureAwait(false));
        var updated = new List<MailContactUpdateOutcome>(command.Updates.Count);
        foreach (var item in command.Updates)
            updated.Add(await UpdateOneAsync(state, item, cancellationToken).ConfigureAwait(false));
        var destroyed = new List<MailContactDestroyOutcome>(command.Destroys.Count);
        foreach (var item in command.Destroys)
            destroyed.Add(DestroyOne(state, item));
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var newState = await states.GetStateAsync(account.InboxId, JmapConstants.ContactCardDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailContactMutationStatus.Ok, oldState, newState,
            created.ToArray(), updated.ToArray(), destroyed.ToArray());
    }

    private async Task<MailContactCreateOutcome> CreateOneAsync(MutationState state,
        MailContactCreate item, CancellationToken cancellationToken)
    {
        if (item.HasId) return CreateError(item.CreationId, MailContactMutationError.InvalidProperties, ["id"]);
        if (item.AddressBookId is not { } bookId || !state.Books.TryGetValue(bookId, out var book))
            return CreateError(item.CreationId, MailContactMutationError.InvalidProperties, ["addressBookIds"]);
        if (state.BookCounts.GetValueOrDefault(bookId) >= environment.Dav.MaxResourcesPerCollection)
            return CreateError(item.CreationId, MailContactMutationError.OverQuota, null);
        var requested = ApplicationValueCodec.Decode(item.Card) as JsonObject
            ?? throw new InvalidOperationException("The contact creation document is invalid.");
        var prepared = await contacts.PrepareCardAsync(state.InboxId, requested, cancellationToken)
            .ConfigureAwait(false);
        if (prepared.Card is null)
            return CreateError(item.CreationId, MailContactMutationError.InvalidProperties,
                prepared.InvalidProperties);
        var uid = prepared.Card["uid"]!.GetValue<string>();
        if (state.UidCounts.GetValueOrDefault(uid) > 0)
            return CreateError(item.CreationId, MailContactMutationError.InvalidProperties, ["uid"]);
        var id = Guid.CreateVersion7();
        var resource = await contacts.StoreCreatedCardAsync(book, id, prepared.Card, DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);
        state.Cards[id] = new JmapContactCardView(resource, prepared.Card);
        state.CreatedIds[item.CreationId] = id;
        state.UidCounts[uid] = state.UidCounts.GetValueOrDefault(uid) + 1;
        state.BookCounts[bookId] = state.BookCounts.GetValueOrDefault(bookId) + 1;
        return new(item.CreationId, id, ApplicationValueCodec.Encode(prepared.Card),
            MailContactMutationError.None, null);
    }

    private async Task<MailContactUpdateOutcome> UpdateOneAsync(MutationState state,
        MailContactUpdate item, CancellationToken cancellationToken)
    {
        var id = Resolve(item.Target, state.CreatedIds);
        if (id is { } destroyId && state.DestroySet.Contains(destroyId))
            return UpdateError(item.RequestedId, MailContactMutationError.WillDestroy, null);
        if (id is not { } cardId || !state.Cards.TryGetValue(cardId, out var existing))
            return UpdateError(item.RequestedId, MailContactMutationError.NotFound, null);
        if (item.Patch is null)
            return UpdateError(item.RequestedId, MailContactMutationError.InvalidPatch, null);
        var current = JmapContactStore.BuildContactCard(existing);
        if (!TryApplyPatch(current, item.Patch, out var patched))
            return UpdateError(item.RequestedId, MailContactMutationError.InvalidPatch, null);
        if (patched["id"] is not JsonValue idNode
            || !idNode.TryGetValue<string>(out var requestedId)
            || !string.Equals(requestedId, existing.Id, StringComparison.Ordinal))
            return UpdateError(item.RequestedId, MailContactMutationError.InvalidProperties, ["id"]);
        if (!TryResolveBook(state, patched["addressBookIds"], out var targetBook))
            return UpdateError(item.RequestedId, MailContactMutationError.InvalidProperties, ["addressBookIds"]);
        if (targetBook!.Id != existing.Resource.CollectionId
            && state.BookCounts.GetValueOrDefault(targetBook.Id) >= environment.Dav.MaxResourcesPerCollection)
            return UpdateError(item.RequestedId, MailContactMutationError.OverQuota, null);
        var requested = (JsonObject)patched.DeepClone();
        requested.Remove("id");
        requested.Remove("addressBookIds");
        var prepared = await contacts.PrepareCardAsync(state.InboxId, requested, cancellationToken)
            .ConfigureAwait(false);
        if (prepared.Card is null)
            return UpdateError(item.RequestedId, MailContactMutationError.InvalidProperties,
                prepared.InvalidProperties);
        var oldUid = existing.Card["uid"]!.GetValue<string>();
        var newUid = prepared.Card["uid"]!.GetValue<string>();
        if (!string.Equals(oldUid, newUid, StringComparison.Ordinal)
            && state.UidCounts.GetValueOrDefault(newUid) > 0)
            return UpdateError(item.RequestedId, MailContactMutationError.InvalidProperties, ["uid"]);
        var oldBook = state.Books[existing.Resource.CollectionId];
        await contacts.StoreUpdatedCardAsync(existing.Resource, oldBook, targetBook, prepared.Card,
            DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        UpdateCounts(state, oldBook, targetBook, oldUid, newUid);
        state.Cards[cardId] = new JmapContactCardView(existing.Resource, prepared.Card);
        return new(item.RequestedId, cardId, ApplicationValueCodec.Encode(requested),
            ApplicationValueCodec.Encode(prepared.Card), MailContactMutationError.None, null);
    }

    private MailContactDestroyOutcome DestroyOne(MutationState state, MailContactDestroy item)
    {
        var id = Resolve(item.Target, state.CreatedIds);
        if (id is not { } cardId || !state.Cards.TryGetValue(cardId, out var existing))
            return new(item.RequestedId, null, MailContactMutationError.NotFound);
        var book = state.Books[existing.Resource.CollectionId];
        contacts.DestroyCard(existing.Resource, book, DateTime.UtcNow);
        state.Cards.Remove(cardId);
        DecrementUidCount(state.UidCounts, existing.Card["uid"]!.GetValue<string>());
        state.BookCounts[book.Id] = state.BookCounts.GetValueOrDefault(book.Id) - 1;
        return new(item.RequestedId, cardId, MailContactMutationError.None);
    }

    private static void UpdateCounts(MutationState state, DavCollectionDB oldBook,
        DavCollectionDB newBook, string oldUid, string newUid)
    {
        if (oldBook.Id != newBook.Id)
        {
            state.BookCounts[oldBook.Id] = state.BookCounts.GetValueOrDefault(oldBook.Id) - 1;
            state.BookCounts[newBook.Id] = state.BookCounts.GetValueOrDefault(newBook.Id) + 1;
        }
        if (!string.Equals(oldUid, newUid, StringComparison.Ordinal))
        {
            DecrementUidCount(state.UidCounts, oldUid);
            state.UidCounts[newUid] = state.UidCounts.GetValueOrDefault(newUid) + 1;
        }
    }

    private static bool TryResolveBook(MutationState state, JsonNode? node,
        out DavCollectionDB? book)
    {
        book = null;
        if (node is not JsonObject ids || ids.Count != 1) return false;
        var item = ids.First();
        if (item.Value is not JsonValue flag || !flag.TryGetValue<bool>(out var included) || !included)
            return false;
        var id = state.AddressBookAliases.GetValueOrDefault(item.Key);
        if (id == Guid.Empty && item.Key.Length == 33 && item.Key[0] == 'D'
            && Guid.TryParseExact(item.Key.AsSpan(1), "N", out var parsed)
            && string.Equals(item.Key, $"D{parsed:N}", StringComparison.Ordinal)) id = parsed;
        return id != Guid.Empty && state.Books.TryGetValue(id, out book);
    }

    private static bool TryApplyPatch(JsonObject source, IReadOnlyList<MailContactPatchEntry> entries,
        out JsonObject patched)
    {
        patched = (JsonObject)source.DeepClone();
        for (var index = 0; index < entries.Count; index++)
        {
            var path = entries[index].Path;
            if (path is null || path.Count == 0 || path.Any(part => string.IsNullOrEmpty(part))
                || entries[index].Value is null) return false;
            for (var previous = 0; previous < index; previous++)
                if (IsPrefix(entries[previous].Path, path) || IsPrefix(path, entries[previous].Path))
                    return false;
        }
        foreach (var entry in entries)
        {
            var parent = patched;
            for (var index = 0; index < entry.Path.Count - 1; index++)
            {
                if (parent[entry.Path[index]] is not JsonObject next) return false;
                parent = next;
            }
            var key = entry.Path[^1];
            var value = ApplicationValueCodec.Decode(entry.Value);
            if (value is null) parent.Remove(key);
            else parent[key] = value;
        }
        return true;
    }

    private static bool IsPrefix(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count > right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static Guid? Resolve(MailContactTarget target, Dictionary<string, Guid> createdIds) =>
        target.CreatedKey is { } key
            ? createdIds.GetValueOrDefault(key) is { } id && id != Guid.Empty ? id : null
            : target.ExistingId;

    private static void DecrementUidCount(Dictionary<string, int> counts, string uid)
    {
        var remaining = counts.GetValueOrDefault(uid) - 1;
        if (remaining > 0) counts[uid] = remaining;
        else counts.Remove(uid);
    }

    private static MailContactCreateOutcome CreateError(string creationId,
        MailContactMutationError error, IReadOnlyList<string>? properties) =>
        new(creationId, null, null, error, properties);

    private static MailContactUpdateOutcome UpdateError(string requestedId,
        MailContactMutationError error, IReadOnlyList<string>? properties) =>
        new(requestedId, null, null, null, error, properties);

    private static MailContactMutationResult Error(MailContactMutationStatus status) =>
        new(status, null, null, [], [], []);

    private sealed class MutationState
    {
        public MutationState(Guid inboxId, List<DavCollectionDB> books,
            List<JmapContactCardView> cards, IReadOnlyDictionary<string, Guid> addressBookAliases,
            IReadOnlyList<MailContactDestroy> destroys)
        {
            InboxId = inboxId;
            Books = books.ToDictionary(book => book.Id);
            Cards = cards.ToDictionary(card => card.Resource.Id);
            AddressBookAliases = addressBookAliases;
            UidCounts = cards.GroupBy(card => card.Card["uid"]!.GetValue<string>(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            BookCounts = cards.GroupBy(card => card.Resource.CollectionId)
                .ToDictionary(group => group.Key, group => group.Count());
            DestroySet = destroys.Where(item => item.Target.ExistingId is not null)
                .Select(item => item.Target.ExistingId!.Value).ToHashSet();
        }

        public Guid InboxId { get; }
        public Dictionary<Guid, DavCollectionDB> Books { get; }
        public Dictionary<Guid, JmapContactCardView> Cards { get; }
        public IReadOnlyDictionary<string, Guid> AddressBookAliases { get; }
        public Dictionary<string, int> UidCounts { get; }
        public Dictionary<Guid, int> BookCounts { get; }
        public HashSet<Guid> DestroySet { get; }
        public Dictionary<string, Guid> CreatedIds { get; } = new(StringComparer.Ordinal);
    }
}
