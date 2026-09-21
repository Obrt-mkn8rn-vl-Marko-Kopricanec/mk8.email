using mk8.email.Contracts.Enums;

namespace mk8.email.Application.Interfaces;

public interface IEmailService
{
    Task<bool> CanReceiveAsync(
        string recipient,
        CancellationToken cancellationToken = default);

    Task<bool> DeliverAsync(
        string sender,
        string recipient,
        string rawMessage,
        string folderName = DefaultFolders.Inbox,
        Guid? queueDeliveryId = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? flags = null,
        bool createFolder = false);

    Task<bool> SaveSentCopyAsync(
        string sender,
        string rawMessage,
        Guid? queueDeliveryId = null,
        CancellationToken cancellationToken = default);
}
