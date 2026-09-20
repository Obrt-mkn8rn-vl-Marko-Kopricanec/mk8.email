namespace mk8.email.Application.Interfaces;

public interface IVacationResponder
{
    Task<bool> QueueResponseAsync(
        string envelopeSender,
        string deliveredRecipient,
        string rawMessage,
        string targetFolder,
        Guid deliveryId,
        CancellationToken cancellationToken = default);
}
