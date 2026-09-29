using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailIdentityMutationService
{
    Task<MailIdentityMutationResult> MutateAsync(
        MailIdentityMutationCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken);
}
