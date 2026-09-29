using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailAddressBookMutationService
{
    Task<MailAddressBookMutationResult> MutateAsync(
        MailAddressBookMutationCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken);
}
