using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailFolderReader(
    JmapAccountService accounts,
    JmapMailboxStore mailboxes,
    JmapStateService states,
    EnvironmentConfig environment) : IMailFolderReader
{
    public async Task<MailFolderReadResult> ReadAsync(
        MailFolderReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailFolderReadStatus.AccountNotFound, null, []);

        if (command.CheckAccountOnly)
            return new(MailFolderReadStatus.Ok, null, []);

        if (command.FolderIds?.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailFolderReadStatus.RequestTooLarge, null, []);

        var all = await mailboxes.LoadAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
        if (command.FolderIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailFolderReadStatus.RequestTooLarge, null, []);

        HashSet<Guid>? requested = command.FolderIds?.ToHashSet();
        var folders = all.Where(folder => requested is null || requested.Contains(folder.Id))
            .Select(folder => new MailFolderSnapshot(
                folder.Id, folder.Name, folder.ParentId, folder.Role, folder.SortOrder,
                folder.IsSubscribed, folder.TotalEmails, folder.UnreadEmails,
                folder.TotalThreads, folder.UnreadThreads, folder.IsProtected))
            .ToArray();
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.MailboxDataType, cancellationToken)
            .ConfigureAwait(false);
        return new(MailFolderReadStatus.Ok, state, folders);
    }

}
