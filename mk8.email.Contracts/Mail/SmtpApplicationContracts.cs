namespace mk8.email.Contracts.Mail;

public interface ISmtpApplicationService
{
    Task<SmtpIdentityResult> AuthenticatePasswordAsync(
        SmtpPasswordAuthentication request,
        CancellationToken cancellationToken = default);

    Task<SmtpIdentityResult> AuthenticateOAuthAsync(
        SmtpOAuthAuthentication request,
        CancellationToken cancellationToken = default);

    Task<bool> CanSendAsAsync(
        SmtpSenderAuthorization request,
        CancellationToken cancellationToken = default);

    Task<bool> HasMatchingFromAddressAsync(
        SmtpFromAddressCheck request,
        CancellationToken cancellationToken = default);

    Task<bool> CanReceiveAsync(
        SmtpRecipientCheck request,
        CancellationToken cancellationToken = default);

    Task<Guid> EnqueueAsync(
        MailSubmission submission,
        CancellationToken cancellationToken = default);
}

public sealed record SmtpPasswordAuthentication(string Username, string Password);

public sealed record SmtpOAuthAuthentication(string AccessToken);

public sealed record SmtpIdentityResult(string? Username);

public sealed record SmtpSenderAuthorization(string AuthenticatedUsername, string SenderAddress);

public sealed record SmtpFromAddressCheck(string RawMessage, string SenderAddress);

public sealed record SmtpRecipientCheck(string Recipient);
