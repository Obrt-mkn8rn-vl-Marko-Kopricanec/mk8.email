using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailAddressBookReader(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IMailAddressBookReader
{
    public async Task<MailAddressBookReadResult> ReadAsync(
        MailAddressBookReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return Error(MailAddressBookReadStatus.AccountNotFound);
        var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        var primary = available.Count == 0 ? null : available[0];
        if (!command.AccountReferenceEligible || primary?.InboxId != account.InboxId)
            return Error(MailAddressBookReadStatus.AccountNotSupported);
        if (command.BookIds?.Count > environment.Jmap.MaxObjectsInGet)
            return Error(MailAddressBookReadStatus.RequestTooLarge);

        await JmapDefaultAddressBookService.EnsureAsync(database, user, cancellationToken)
            .ConfigureAwait(false);
        var all = await database.DavCollections.AsNoTracking()
            .Where(book => book.UserId == user.Id && book.CollectionType == DavCollectionDB.AddressBookType)
            .OrderBy(book => book.SortOrder)
            .ThenBy(book => book.DisplayName)
            .ThenBy(book => book.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (command.BookIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return Error(MailAddressBookReadStatus.RequestTooLarge);

        HashSet<Guid>? requested = command.BookIds?.ToHashSet();
        var books = all.Where(book => requested is null || requested.Contains(book.Id))
            .Select(book => new MailAddressBookSnapshot(book.Id, book.DisplayName, book.Description,
                book.SortOrder, book.IsDefault, book.IsSubscribed,
                book.IsDefault || string.Equals(book.Slug, "default", StringComparison.Ordinal)))
            .ToArray();
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.AddressBookDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailAddressBookReadStatus.Ok, state, books);
    }

    private static MailAddressBookReadResult Error(MailAddressBookReadStatus status) => new(status, null, []);
}
