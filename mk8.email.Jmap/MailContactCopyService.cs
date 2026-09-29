using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailContactCopyService(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states) : IMailContactCopyService
{
    public async Task<MailContactCopyResult> CopyAsync(
        MailContactCopyCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var source = command.SourceReferenceParseable
            ? await accounts.GetAccountByInboxIdAsync(user, command.SourceAccountId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (source is null)
            return Error(MailContactCopyStatus.FromAccountNotFound);
        var available = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        var primary = available.Count == 0 ? null : available[0];
        if (!command.SourceReferenceEligible || primary?.InboxId != source.InboxId)
            return Error(MailContactCopyStatus.FromAccountNotSupported);

        var target = command.TargetReferenceParseable
            ? await accounts.GetAccountByInboxIdAsync(user, command.TargetAccountId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (target is null)
            return Error(MailContactCopyStatus.AccountNotFound);
        if (!command.TargetReferenceEligible || primary!.InboxId != target.InboxId)
            return Error(MailContactCopyStatus.AccountNotSupported);

        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken).ConfigureAwait(false);
        var sourceState = await states.GetStateAsync(source.InboxId, JmapConstants.ContactCardDataType,
            cancellationToken).ConfigureAwait(false);
        var targetState = await states.GetStateAsync(target.InboxId, JmapConstants.ContactCardDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfFromInState is not null
                && !string.Equals(command.IfFromInState, sourceState, StringComparison.Ordinal)
            || command.IfInState is not null
                && !string.Equals(command.IfInState, targetState, StringComparison.Ordinal))
            return Error(MailContactCopyStatus.StateMismatch);
        return new(MailContactCopyStatus.Ok, targetState);
    }

    private static MailContactCopyResult Error(MailContactCopyStatus status) => new(status, null);
}
