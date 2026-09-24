namespace mk8.email.Application.Interfaces;

public interface IMailAuthenticator
{
    Task<AuthenticatedMailUser?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);

    Task<AuthenticatedMailUser?> AuthenticatePrimaryAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        AuthenticateAsync(username, password, cancellationToken);
}
