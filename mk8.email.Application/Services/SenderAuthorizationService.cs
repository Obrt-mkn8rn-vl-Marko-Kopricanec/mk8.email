using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Application.Services;

public sealed class SenderAuthorizationService(EmailDbContext db) : ISenderAuthorizationService
{
    public async Task<bool> CanSendAsAsync(
        string authenticatedUsername,
        string senderAddress,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetIdentity(senderAddress, out var senderLocalPart, out var senderDomain))
            return false;

        var identities = await db.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.Owner.Username == authenticatedUsername
                && inbox.Owner.IsActive
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive)
            .Select(inbox => new { inbox.Name, inbox.Address.Domain })
            .ToListAsync(cancellationToken);

        return identities.Any(identity =>
            string.Equals(identity.Name, senderLocalPart, StringComparison.OrdinalIgnoreCase)
            && string.Equals(identity.Domain, senderDomain, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasMatchingFromAddress(string rawMessage, string senderAddress)
    {
        if (!TryGetIdentity(senderAddress, out var senderLocalPart, out var senderDomain))
            return false;

        try
        {
            using var input = new MemoryStream(
                MailWireEncoding.Instance.GetBytes(rawMessage),
                writable: false);
            using var message = MimeMessage.Load(input);
            if (message.Headers.Count(header => header.Id == HeaderId.From) != 1
                || message.From.Count != 1
                || message.From[0] is not MailboxAddress from
                || !MatchesIdentity(from.Address, senderLocalPart, senderDomain))
            {
                return false;
            }

            return message.Headers.Count(header => header.Id == HeaderId.Sender) <= 1
                && (message.Sender is null
                    || MatchesIdentity(message.Sender.Address, senderLocalPart, senderDomain));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool MatchesIdentity(string address, string expectedLocalPart, string expectedDomain) =>
        TryGetIdentity(address, out var localPart, out var domain)
        && string.Equals(localPart, expectedLocalPart, StringComparison.OrdinalIgnoreCase)
        && string.Equals(domain, expectedDomain, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetIdentity(string address, out string localPart, out string domain)
    {
        localPart = string.Empty;
        domain = string.Empty;

        if (string.IsNullOrWhiteSpace(address)
            || address.Length > 254
            || address.Any(character => !char.IsAscii(character))
            || !MailboxAddress.TryParse(address, out var mailbox)
            || !string.Equals(address, mailbox.Address, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = mailbox.Address.LastIndexOf('@');
        if (separator is < 1 or > 64 || separator == mailbox.Address.Length - 1)
            return false;

        localPart = mailbox.Address[..separator];
        domain = mailbox.Address[(separator + 1)..];
        return DkimIdentityValidator.IsValidDomain(domain);
    }
}
