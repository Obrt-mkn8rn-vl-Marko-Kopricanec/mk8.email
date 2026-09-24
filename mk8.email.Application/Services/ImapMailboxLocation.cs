namespace mk8.email.Application.Services;

internal readonly record struct ImapMailboxLocation(Guid InboxId, string FolderName);
