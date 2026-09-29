using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailAddressBookReader
{
    Task<MailAddressBookReadResult> ReadAsync(
        MailAddressBookReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
