using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailChangesReader
{
    Task<MailChangesResult> ReadAsync(
        MailOperationKind operation,
        MailChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
