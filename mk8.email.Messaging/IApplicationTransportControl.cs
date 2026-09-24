namespace mk8.email.Messaging;

public interface IApplicationTransportControl
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
