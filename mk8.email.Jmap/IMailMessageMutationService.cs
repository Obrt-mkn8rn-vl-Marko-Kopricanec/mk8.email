using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailMessageMutationService
{
    Task<MailMessageMutationResult> MutateAsync(
        MailMessageMutationCommand command,
        AuthenticatedMailUser user,
        JmapInvocationContext context,
        CancellationToken cancellationToken);
}
