using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailSearchSnippetService
{
    Task<MailSearchSnippetResult> ReadAsync(
        MailSearchSnippetCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken);
}
