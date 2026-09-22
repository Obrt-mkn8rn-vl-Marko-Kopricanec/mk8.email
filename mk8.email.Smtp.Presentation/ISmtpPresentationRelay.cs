using mk8.email.Contracts.Mail;

namespace mk8.email.Smtp.Presentation;

public interface ISmtpPresentationRelay
{
    Task<OutboundDeliveryResult> RelayAsync(
        SmtpRelayPresentationRequest request,
        Guid applicationRequestId,
        CancellationToken cancellationToken);
}
