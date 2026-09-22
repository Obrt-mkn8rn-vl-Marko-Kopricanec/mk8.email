namespace mk8.email.Contracts.Mail;

public enum OutboundDeliveryStatus
{
    Delivered,
    TemporaryFailure,
    PermanentFailure,
}

public sealed record OutboundDeliveryResult(
    OutboundDeliveryStatus Status,
    string Detail,
    bool DsnParametersForwarded = false,
    string? RemoteMta = null,
    string? EnhancedStatusCode = null);

public sealed record OutboundMailOptions(
    bool RequiresSmtpUtf8 = false,
    MailDsnEnvelope? Dsn = null,
    MailDsnRecipient? RecipientDsn = null);

public interface IOutboundMailRelay
{
    Task<OutboundDeliveryResult> RelayAsync(
        string sender,
        string recipient,
        string rawMessage,
        OutboundMailOptions? options = null,
        CancellationToken cancellationToken = default);
}
