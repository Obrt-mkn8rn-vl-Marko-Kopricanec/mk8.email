namespace mk8.email.Messaging.Tests;

internal sealed class SmtpListenerScope(SmtpListenerFixture owner) : ISmtpApplicationService, IDisposable
{
    public Task<SmtpIdentityResult> AuthenticatePasswordAsync(SmtpPasswordAuthentication request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Listener lifetime controls do not authenticate.");
    public Task<SmtpIdentityResult> AuthenticateOAuthAsync(SmtpOAuthAuthentication request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Listener lifetime controls do not authenticate.");
    public Task<bool> CanSendAsAsync(SmtpSenderAuthorization request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Listener lifetime controls do not authorize senders.");
    public Task<bool> HasMatchingFromAddressAsync(SmtpFromAddressCheck request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Listener lifetime controls do not inspect MIME.");
    public Task<bool> CanReceiveAsync(SmtpRecipientCheck request, CancellationToken cancellationToken = default) =>
        owner.RecipientHold.CheckAsync(cancellationToken);
    public Task<Guid> EnqueueAsync(MailSubmission submission, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Listener lifetime controls cannot enqueue mail.");

    public void Dispose() => owner.RecordScopeDisposal();
}
