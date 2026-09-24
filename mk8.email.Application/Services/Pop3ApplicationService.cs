using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Pop3;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal sealed class Pop3ApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens,
    EmailDbContext database,
    MailboxMessageContentService content,
    LargeObjectTransactionEffects transactionEffects,
    ILogger<Pop3ApplicationService> logger) : IPop3ApplicationService
{
    public async Task<Pop3IdentityResult> AuthenticatePasswordAsync(
        Pop3PasswordAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await authenticator.AuthenticateAsync(
            request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        return user is null
            ? new Pop3IdentityResult(null, null)
            : new Pop3IdentityResult(user.Id, user.Username);
    }

    public async Task<Pop3IdentityResult> AuthenticateOAuthAsync(
        Pop3OAuthAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await oauthTokens.AuthenticateAccessTokenAsync(
            request.AccessToken, "pop", cancellationToken).ConfigureAwait(false);
        return user is null
            || !string.Equals(request.Username, user.Username, StringComparison.OrdinalIgnoreCase)
            ? new Pop3IdentityResult(null, null)
            : new Pop3IdentityResult(user.Id, user.Username);
    }

    public async Task<Pop3MaildropSnapshot> ListMaildropAsync(
        Pop3UserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("The POP3 user identifier is invalid.", nameof(request));
        var folderId = await GetPrimaryInboxFolderIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        var messages = new List<Pop3MessageSummary>();
        var query = database.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == folderId && !email.IsDeleted)
            .OrderBy(email => email.Uid);
        await foreach (var email in query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
            messages.Add(new Pop3MessageSummary(
                email.Id,
                email.Uid,
                Pop3WireCodec.GetNormalizedCrlfLength(raw)));
        }
        return new Pop3MaildropSnapshot(messages);
    }

    public async Task<Pop3MessageResult> GetMessageAsync(
        Pop3MessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty || request.MessageId == Guid.Empty)
            throw new ArgumentException("The POP3 message identity is invalid.", nameof(request));
        var folderId = await GetPrimaryInboxFolderIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var email = await database.Emails
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.MessageId
                && candidate.FolderId == folderId
                && !candidate.IsDeleted)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new Pop3MessageResult(email is null
            ? null
            : await content.ReadAsync(email, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Pop3DeleteResult> CommitDeletesAsync(
        Pop3DeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MessageIds);
        if (request.UserId == Guid.Empty || request.MessageIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("The POP3 deletion identity is invalid.", nameof(request));
        var messageIds = request.MessageIds.Distinct().ToArray();
        if (messageIds.Length == 0)
            return new Pop3DeleteResult(0);
        var folderId = await GetPrimaryInboxFolderIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        var marker = transactionEffects.Mark();
        var commitAttempted = false;
        var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
.ConfigureAwait(false) : null;
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            try
            {
                var emails = await database.Emails
                    .Where(email => messageIds.Contains(email.Id)
                        && email.FolderId == folderId)
                    .OrderBy(email => email.FolderId)
                    .ThenBy(email => email.Uid)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var folderIds = emails.Select(email => email.FolderId).Distinct().ToArray();
                var folders = await database.Folders
                    .Where(folder => folderIds.Contains(folder.Id))
                    .ToDictionaryAsync(folder => folder.Id, cancellationToken).ConfigureAwait(false);

                foreach (var email in emails)
                {
                    var folder = folders[email.FolderId];
                    await database.ExpungedUids.AddAsync(new ExpungedUidDB
                    {
                        Id = Guid.CreateVersion7(),
                        Uid = email.Uid,
                        ModSeq = ++folder.HighestModSeq,
                        FolderId = folder.Id,
                    }, cancellationToken).ConfigureAwait(false);
                    content.DeleteOnCommit(email);
                    database.Emails.Remove(email);
                }

                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                await transactionEffects.CommitAsync(marker).ConfigureAwait(false);
                return new Pop3DeleteResult(emails.Count);
            }
            catch
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        logger.LogWarning(rollbackException, "Could not roll back POP3 deletion");
                    }
                }
                if (commitAttempted)
                    transactionEffects.Discard(marker);
                else
                    await transactionEffects.RollbackAsync(marker).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<Guid> GetPrimaryInboxFolderIdAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var username = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.IsActive)
            .Select(user => user.Username)
            .SingleOrDefaultAsync(cancellationToken)
.ConfigureAwait(false) ?? throw new InvalidOperationException("The POP3 account is unavailable.");
        var separator = username.LastIndexOf('@');
        if (separator <= 0 || separator == username.Length - 1)
            throw new InvalidOperationException("The POP3 account has no primary mailbox.");
        var localPart = username[..separator];
        var domain = username[(separator + 1)..];
        return await database.Folders
            .AsNoTracking()
            .Where(folder => folder.Inbox.OwnerId == userId
                && folder.Inbox.AliasForInboxId == null
                && folder.Inbox.Name == localPart
                && folder.Inbox.Address.Domain == domain
                && folder.Inbox.Address.IsActive
                && folder.Inbox.Address.Company.IsActive
                && folder.Name == DefaultFolders.Inbox)
            .Select(folder => (Guid?)folder.Id)
            .SingleOrDefaultAsync(cancellationToken)
.ConfigureAwait(false) ?? throw new InvalidOperationException("The POP3 account has no INBOX.");
    }
}
