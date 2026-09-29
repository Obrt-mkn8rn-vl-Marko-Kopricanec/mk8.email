using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailSearchSnippetService(
    EmailDbContext database,
    JmapAccountService accounts,
    MailboxMessageContentService content) : IMailSearchSnippetService
{
    public async Task<MailSearchSnippetResult> ReadAsync(
        MailSearchSnippetCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailSearchSnippetStatus.AccountNotFound, []);
        if (command.CheckAccountOnly)
            return new(MailSearchSnippetStatus.Authorized, []);
        var emails = await database.Emails.AsNoTracking()
            .Where(email => email.Folder.InboxId == account.InboxId && !email.IsDeleted
                && command.MessageIds.Contains(email.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var snippets = new List<MailSearchSnippetSnapshot>(emails.Count);
        for (var emailIndex = 0; emailIndex < emails.Count; emailIndex++)
        {
            var email = emails[emailIndex];
            var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
            using var message = JmapEmailCodec.Parse(raw);
            snippets.Add(new(email.Id,
                MailSearchSnippetText.SelectSubject(JmapEmailCodec.LastTextHeader(message, "Subject"),
                    command.Terms),
                MailSearchSnippetText.SelectPreview(JmapEmailCodec.SearchableBodyText(message),
                    command.Terms)));
        }
        return new(MailSearchSnippetStatus.Ok, snippets);
    }
}
