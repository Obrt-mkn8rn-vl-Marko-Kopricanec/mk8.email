using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Pop3;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Pop3;

public sealed class GatewayPop3ApplicationService(
    IGatewayApplicationTransport transport) : IPop3ApplicationService
{
    public Task<Pop3IdentityResult> AuthenticatePasswordAsync(
        Pop3PasswordAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<Pop3PasswordAuthentication, Pop3IdentityResult>(
            "pop3", ApplicationOperations.Pop3AuthenticatePassword, request, cancellationToken);

    public Task<Pop3IdentityResult> AuthenticateOAuthAsync(
        Pop3OAuthAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<Pop3OAuthAuthentication, Pop3IdentityResult>(
            "pop3", ApplicationOperations.Pop3AuthenticateOAuth, request, cancellationToken);

    public Task<Pop3MaildropSnapshot> ListMaildropAsync(
        Pop3UserRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<Pop3UserRequest, Pop3MaildropSnapshot>(
            "pop3", ApplicationOperations.Pop3ListMaildrop, request, cancellationToken);

    public Task<Pop3MessageResult> GetMessageAsync(
        Pop3MessageRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<Pop3MessageRequest, Pop3MessageResult>(
            "pop3", ApplicationOperations.Pop3GetMessage, request, cancellationToken);

    public Task<Pop3DeleteResult> CommitDeletesAsync(
        Pop3DeleteRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<Pop3DeleteRequest, Pop3DeleteResult>(
            "pop3", ApplicationOperations.Pop3CommitDeletes, request, cancellationToken);
}
