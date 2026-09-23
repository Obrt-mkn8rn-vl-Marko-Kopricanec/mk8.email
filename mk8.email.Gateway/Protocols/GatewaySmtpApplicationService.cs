using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols;

public sealed class GatewaySmtpApplicationService(
    IGatewayApplicationTransport transport) : ISmtpApplicationService
{
    public Task<SmtpIdentityResult> AuthenticatePasswordAsync(
        SmtpPasswordAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SmtpPasswordAuthentication, SmtpIdentityResult>(
            "smtp", ApplicationOperations.SmtpAuthenticatePassword, request, cancellationToken);

    public Task<SmtpIdentityResult> AuthenticateOAuthAsync(
        SmtpOAuthAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SmtpOAuthAuthentication, SmtpIdentityResult>(
            "smtp", ApplicationOperations.SmtpAuthenticateOAuth, request, cancellationToken);

    public Task<bool> CanSendAsAsync(
        SmtpSenderAuthorization request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SmtpSenderAuthorization, bool>(
            "smtp", ApplicationOperations.SmtpCanSendAs, request, cancellationToken);

    public Task<bool> HasMatchingFromAddressAsync(
        SmtpFromAddressCheck request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SmtpFromAddressCheck, bool>(
            "smtp", ApplicationOperations.SmtpHasMatchingFromAddress, request, cancellationToken);

    public Task<bool> CanReceiveAsync(
        SmtpRecipientCheck request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SmtpRecipientCheck, bool>(
            "smtp", ApplicationOperations.SmtpCanReceive, request, cancellationToken);

    public Task<Guid> EnqueueAsync(
        MailSubmission submission,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<MailSubmission, Guid>(
            "smtp", ApplicationOperations.SmtpEnqueue, submission, cancellationToken);
}
