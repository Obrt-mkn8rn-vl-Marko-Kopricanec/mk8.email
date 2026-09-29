using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailImportService
{
    Task<MailImportResult> ImportAsync(MailImportCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken);
}
