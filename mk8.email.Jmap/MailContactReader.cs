using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailContactReader(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IMailContactReader
{
    public async Task<MailContactReadResult> ReadAsync(
        MailContactReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = command.AccountReferenceParseable
            ? await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (account is null)
            return Error(MailContactReadStatus.AccountNotFound);
        var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        if (!command.AccountReferenceEligible || available.Count == 0
            || available[0].InboxId != account.InboxId)
            return Error(MailContactReadStatus.AccountNotSupported);
        if (command.CardIds?.Count > environment.Jmap.MaxObjectsInGet)
            return Error(MailContactReadStatus.RequestTooLarge);
        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken).ConfigureAwait(false);
        var all = await contacts.LoadCardsAsync(account.UserId, false, cancellationToken).ConfigureAwait(false);
        if (command.CardIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return Error(MailContactReadStatus.RequestTooLarge);
        var requested = command.CardIds?.ToHashSet();
        var snapshots = all.Where(card => requested is null || requested.Contains(card.Resource.Id))
            .Select(card => new MailContactCardSnapshot(card.Resource.Id,
                card.Resource.CollectionId, card.Resource.Uid,
                card.Card.ToJsonString(JmapJson.SerializerOptions)))
            .ToArray();
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.ContactCardDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailContactReadStatus.Ok, state, snapshots);
    }

    private static MailContactReadResult Error(MailContactReadStatus status) => new(status, null, []);
}
