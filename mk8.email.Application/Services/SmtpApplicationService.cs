using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Mail;

namespace mk8.email.Application.Services;

public sealed class SmtpApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens,
    ISenderAuthorizationService senderAuthorization,
    IEmailService emailService,
    IMailSubmissionQueue queue) : ISmtpApplicationService
{
    public async Task<SmtpIdentityResult> AuthenticatePasswordAsync(
        SmtpPasswordAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await authenticator.AuthenticateAsync(
            request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        return new SmtpIdentityResult(user?.Username);
    }

    public async Task<SmtpIdentityResult> AuthenticateOAuthAsync(
        SmtpOAuthAuthentication request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await oauthTokens.AuthenticateAccessTokenAsync(
            request.AccessToken, "smtp", cancellationToken).ConfigureAwait(false);
        return new SmtpIdentityResult(user?.Username);
    }

    public Task<bool> CanSendAsAsync(
        SmtpSenderAuthorization request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return senderAuthorization.CanSendAsAsync(
            request.AuthenticatedUsername, request.SenderAddress, cancellationToken);
    }

    public Task<bool> HasMatchingFromAddressAsync(
        SmtpFromAddressCheck request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(senderAuthorization.HasMatchingFromAddress(
            request.RawMessage, request.SenderAddress));
    }

    public Task<bool> CanReceiveAsync(
        SmtpRecipientCheck request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return emailService.CanReceiveAsync(request.Recipient, cancellationToken);
    }

    public Task<Guid> EnqueueAsync(
        MailSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return queue.EnqueueAsync(submission, cancellationToken);
    }
}
