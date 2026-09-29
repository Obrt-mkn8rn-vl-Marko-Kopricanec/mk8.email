using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailAddressBookMutationService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IMailAddressBookMutationService
{
    public async Task<MailAddressBookMutationResult> MutateAsync(
        MailAddressBookMutationCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return Empty(MailAddressBookMutationStatus.AccountNotFound);
        var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        if (!command.AccountReferenceEligible || available.Count == 0 || available[0].InboxId != account.InboxId)
            return Empty(MailAddressBookMutationStatus.AccountNotSupported);
        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(account.InboxId, JmapConstants.AddressBookDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return Empty(MailAddressBookMutationStatus.StateMismatch);
        return await ApplyAsync(command, account, oldState, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MailAddressBookMutationResult> ApplyAsync(
        MailAddressBookMutationCommand command, JmapAccount account, string oldState,
        CancellationToken cancellationToken)
    {
        var books = await contacts.LoadAddressBooksAsync(account.UserId, true, cancellationToken)
            .ConfigureAwait(false);
        var byId = books.ToDictionary(book => book.Id);
        var createdIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var created = new List<MailAddressBookCreateOutcome>(command.Creates.Count);
        var updated = new List<MailAddressBookUpdateOutcome>(command.Updates.Count);
        var destroyed = new List<MailAddressBookDestroyOutcome>(command.Destroys.Count);
        var collectionCount = await database.DavCollections.CountAsync(collection =>
            collection.UserId == account.UserId && collection.Slug != "schedule-inbox"
            && collection.Slug != "schedule-outbox", cancellationToken).ConfigureAwait(false);
        var remainingCapacity = Math.Max(0, environment.Dav.MaxCollectionsPerUser - collectionCount);
        var now = DateTime.UtcNow;

        foreach (var item in command.Creates)
        {
            var outcome = await CreateOneAsync(item, account.UserId, remainingCapacity, now,
                cancellationToken).ConfigureAwait(false);
            created.Add(outcome);
            if (outcome.Book is null) continue;
            var book = database.DavCollections.Local.First(candidate => candidate.Id == outcome.Book.Id);
            books.Add(book);
            byId[book.Id] = book;
            createdIds[item.CreationId] = book.Id;
            remainingCapacity--;
        }

        var destroyIds = command.Destroys.Select(item => Resolve(item.Target, createdIds))
            .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        foreach (var item in command.Updates)
            updated.Add(UpdateOne(item, byId, createdIds, destroyIds, now));
        foreach (var item in command.Destroys)
            destroyed.Add(await DestroyOneAsync(item, byId, createdIds, command.OnDestroyRemoveContents,
                cancellationToken).ConfigureAwait(false));

        var defaultChanges = new List<MailAddressBookSnapshot>();
        if (created.All(item => item.Error == MailAddressBookMutationError.None)
            && updated.All(item => item.Error == MailAddressBookMutationError.None)
            && destroyed.All(item => item.Error == MailAddressBookMutationError.None))
        {
            SetDefault(command.OnSuccessSetIsDefault, books, byId, createdIds, now, defaultChanges);
        }
        for (var index = 0; index < created.Count; index++)
        {
            if (created[index].Book is { } snapshot && byId.TryGetValue(snapshot.Id, out var book))
                created[index] = created[index] with { Book = Snapshot(book) };
        }
        for (var index = 0; index < updated.Count; index++)
        {
            if (updated[index].Book is { } snapshot && byId.TryGetValue(snapshot.Id, out var book))
                updated[index] = updated[index] with { Book = Snapshot(book) };
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var newState = await states.GetStateAsync(account.InboxId, JmapConstants.AddressBookDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailAddressBookMutationStatus.Ok, oldState, newState,
            created, updated, destroyed, defaultChanges);
    }

    private async Task<MailAddressBookCreateOutcome> CreateOneAsync(
        MailAddressBookCreate item, Guid userId, int remainingCapacity, DateTime now, CancellationToken token)
    {
        if (remainingCapacity == 0)
            return CreateError(item, MailAddressBookMutationError.OverQuota);
        if (item.HasNonNullShareWith)
            return CreateError(item, MailAddressBookMutationError.Forbidden);
        if (item.Values is null)
            return CreateError(item, MailAddressBookMutationError.Skipped);
        var invalid = Validate(item.Values, out var normalizedName);
        if (invalid.Count > 0)
            return CreateError(item, MailAddressBookMutationError.InvalidProperties, invalid);
        var id = Guid.CreateVersion7();
        var book = new DavCollectionDB
        {
            Id = id,
            UserId = userId,
            CollectionType = DavCollectionDB.AddressBookType,
            Slug = "jmap-" + id.ToString("N"),
            DisplayName = normalizedName!,
            Description = item.Values.Description,
            SortOrder = checked((int)item.Values.SortOrder),
            IsDefault = false,
            IsSubscribed = item.Values.IsSubscribed,
            Components = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        await database.DavCollections.AddAsync(book, token).ConfigureAwait(false);
        return new(item.CreationId, Snapshot(book), MailAddressBookMutationError.None, null);
    }

    private static MailAddressBookUpdateOutcome UpdateOne(
        MailAddressBookUpdate item, Dictionary<Guid, DavCollectionDB> byId,
        Dictionary<string, Guid> createdIds, HashSet<Guid> destroyIds, DateTime now)
    {
        var id = Resolve(item.Target, createdIds);
        if (id is { } doomed && destroyIds.Contains(doomed))
            return UpdateError(item, MailAddressBookMutationError.WillDestroy);
        if (id is null || !byId.TryGetValue(id.Value, out var book))
            return UpdateError(item, MailAddressBookMutationError.NotFound);
        if (item.Patch is null)
            return UpdateError(item, MailAddressBookMutationError.Skipped);
        var invalid = ImmutableMismatches(book, item.Patch);
        if (invalid.Count > 0)
            return UpdateError(item, MailAddressBookMutationError.InvalidProperties, invalid);
        if (item.Patch.HasNonNullShareWith)
            return UpdateError(item, MailAddressBookMutationError.Forbidden);
        var revised = new MailAddressBookValues(
            item.Patch.SetName ? item.Patch.Name : book.DisplayName,
            item.Patch.SetDescription ? item.Patch.Description : book.Description,
            item.Patch.SetSortOrder ? item.Patch.SortOrder ?? 0 : book.SortOrder,
            item.Patch.SetIsSubscribed ? item.Patch.IsSubscribed ?? true : book.IsSubscribed);
        invalid = Validate(revised, out var normalizedName);
        if (invalid.Count > 0)
            return UpdateError(item, MailAddressBookMutationError.InvalidProperties, invalid);
        book.DisplayName = normalizedName!;
        book.Description = revised.Description;
        book.SortOrder = checked((int)revised.SortOrder);
        book.IsSubscribed = revised.IsSubscribed;
        book.UpdatedAt = now;
        return new(item.RequestedId, Snapshot(book), MailAddressBookMutationError.None, null);
    }

    private async Task<MailAddressBookDestroyOutcome> DestroyOneAsync(
        MailAddressBookDestroy item, Dictionary<Guid, DavCollectionDB> byId,
        Dictionary<string, Guid> createdIds, bool removeContents, CancellationToken token)
    {
        var id = Resolve(item.Target, createdIds);
        if (id is null || !byId.TryGetValue(id.Value, out var book))
            return new(item.RequestedId, null, MailAddressBookMutationError.NotFound);
        if (IsProtected(book))
            return new(item.RequestedId, null, MailAddressBookMutationError.Forbidden);
        var resources = await database.DavResources.Where(resource => resource.CollectionId == book.Id)
            .ToListAsync(token).ConfigureAwait(false);
        if (resources.Count > 0 && !removeContents)
            return new(item.RequestedId, null, MailAddressBookMutationError.AddressBookHasContents);
        if (resources.Count > 0) database.DavResources.RemoveRange(resources);
        database.DavCollections.Remove(book);
        byId.Remove(book.Id);
        return new(item.RequestedId, book.Id, MailAddressBookMutationError.None);
    }

    private static void SetDefault(
        MailAddressBookTarget? target, List<DavCollectionDB> books,
        Dictionary<Guid, DavCollectionDB> byId, Dictionary<string, Guid> createdIds,
        DateTime now, List<MailAddressBookSnapshot> changes)
    {
        if (target is null || Resolve(target, createdIds) is not { } id
            || !byId.TryGetValue(id, out var nextDefault)) return;
        foreach (var book in books.Where(book => book.IsDefault && book.Id != id && byId.ContainsKey(book.Id)))
        {
            book.IsDefault = false;
            book.UpdatedAt = now;
            changes.Add(Snapshot(book));
        }
        if (nextDefault.IsDefault) return;
        nextDefault.IsDefault = true;
        nextDefault.UpdatedAt = now;
        changes.Add(Snapshot(nextDefault));
    }

    private static List<MailAddressBookField> ImmutableMismatches(DavCollectionDB book, MailAddressBookPatch patch)
    {
        var invalid = new List<MailAddressBookField>(3);
        if (patch.AssertId && !string.Equals(patch.Id, $"D{book.Id:N}", StringComparison.Ordinal))
            invalid.Add(MailAddressBookField.Id);
        if (patch.AssertIsDefault && patch.IsDefault != book.IsDefault)
            invalid.Add(MailAddressBookField.IsDefault);
        if (patch.Rights is { } rights && (rights.CheckMayRead && rights.MayRead != true
            || rights.CheckMayWrite && rights.MayWrite != true
            || rights.CheckMayShare && rights.MayShare != false
            || rights.CheckMayDelete && rights.MayDelete != !IsProtected(book)))
            invalid.Add(MailAddressBookField.MyRights);
        return invalid;
    }

    private static List<MailAddressBookField> Validate(MailAddressBookValues values, out string? normalizedName)
    {
        var invalid = new List<MailAddressBookField>(4);
        normalizedName = values.Name;
        if (string.IsNullOrEmpty(values.Name) || !JmapJson.ContainsOnlyUnicodeScalars(values.Name))
            invalid.Add(MailAddressBookField.Name);
        else
        {
            try
            {
                normalizedName = values.Name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                invalid.Add(MailAddressBookField.Name);
            }
            if (normalizedName is not null && Encoding.UTF8.GetByteCount(normalizedName) > 255)
                invalid.Add(MailAddressBookField.Name);
        }
        if (values.Description is { Length: > 1024 })
            invalid.Add(MailAddressBookField.Description);
        if (values.SortOrder is < 0 or > int.MaxValue)
            invalid.Add(MailAddressBookField.SortOrder);
        return invalid.Distinct().ToList();
    }

    private static bool IsProtected(DavCollectionDB book) =>
        book.IsDefault || string.Equals(book.Slug, "default", StringComparison.Ordinal);

    private static MailAddressBookSnapshot Snapshot(DavCollectionDB book) =>
        new(book.Id, book.DisplayName, book.Description, book.SortOrder,
            book.IsDefault, book.IsSubscribed, IsProtected(book));

    private static Guid? Resolve(MailAddressBookTarget target, Dictionary<string, Guid> createdIds) =>
        target.ExistingId ?? (target.CreatedKey is not null
            && createdIds.TryGetValue(target.CreatedKey, out var id) ? id : null);

    private static MailAddressBookCreateOutcome CreateError(MailAddressBookCreate item,
        MailAddressBookMutationError error, IReadOnlyList<MailAddressBookField>? invalid = null) =>
        new(item.CreationId, null, error, invalid);

    private static MailAddressBookUpdateOutcome UpdateError(MailAddressBookUpdate item,
        MailAddressBookMutationError error, IReadOnlyList<MailAddressBookField>? invalid = null) =>
        new(item.RequestedId, null, error, invalid);

    private static MailAddressBookMutationResult Empty(MailAddressBookMutationStatus status) =>
        new(status, null, null, [], [], [], []);
}
