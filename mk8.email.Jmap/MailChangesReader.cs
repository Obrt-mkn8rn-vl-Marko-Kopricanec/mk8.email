using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailChangesReader(
    JmapAccountService accounts,
    JmapStateService states,
    JmapIdentityService identities,
    mk8.email.Infrastructure.Data.EmailDbContext database,
    EnvironmentConfig environment) : IMailChangesReader
{
    public async Task<MailChangesResult> ReadAsync(
        MailOperationKind operation,
        MailChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (!MailChangeOperations.TryGetFeature(operation, out _))
            throw new ArgumentOutOfRangeException(nameof(operation));
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return Error(MailChangesStatus.AccountNotFound);

        if (operation is MailOperationKind.ReadAddressBookChanges or MailOperationKind.ReadContactChanges)
        {
            var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
            var primary = available.Count == 0 ? null : available[0];
            if (!command.AccountReferenceEligible || primary?.InboxId != account.InboxId)
                return Error(MailChangesStatus.AccountNotSupported);
            await JmapDefaultAddressBookService.EnsureAsync(database, user, cancellationToken).ConfigureAwait(false);
        }
        else if (operation == MailOperationKind.ReadSenderIdentityChanges)
            await identities.EnsureDefaultAsync(account, cancellationToken).ConfigureAwait(false);

        var dataType = operation switch
        {
            MailOperationKind.ReadFolderChanges => JmapConstants.MailboxDataType,
            MailOperationKind.ReadThreadChanges => JmapConstants.ThreadDataType,
            MailOperationKind.ReadMessageChanges => JmapConstants.EmailDataType,
            MailOperationKind.ReadSenderIdentityChanges => JmapConstants.IdentityDataType,
            MailOperationKind.ReadSubmissionChanges => JmapConstants.EmailSubmissionDataType,
            MailOperationKind.ReadAddressBookChanges => JmapConstants.AddressBookDataType,
            MailOperationKind.ReadContactChanges => JmapConstants.ContactCardDataType,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var changes = await states.GetChangesAsync(account.InboxId, dataType,
            command.SinceState, command.MaxChanges, environment.Jmap.MaxObjectsInGet, cancellationToken)
            .ConfigureAwait(false);
        return changes is null
            ? Error(MailChangesStatus.CannotCalculateChanges)
            : new(MailChangesStatus.Ok, changes.OldState, changes.NewState,
                changes.HasMoreChanges, changes.Created, changes.Updated, changes.Destroyed);
    }

    private static MailChangesResult Error(MailChangesStatus status) => new(status, null, null, false, [], [], []);
}
