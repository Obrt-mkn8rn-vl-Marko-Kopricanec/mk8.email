namespace mk8.email.Application.Interfaces;

public enum OutboundDeliveryStatus
{
    Delivered,
    TemporaryFailure,
    PermanentFailure,
}

public sealed record OutboundDeliveryResult(
    OutboundDeliveryStatus Status,
    string Detail);

public sealed record OutboundMailOptions(bool RequiresSmtpUtf8 = false);

public interface IOutboundMailRelay
{
    Task<OutboundDeliveryResult> RelayAsync(
        string sender,
        string recipient,
        string rawMessage,
        OutboundMailOptions? options = null,
        CancellationToken cancellationToken = default);
}
