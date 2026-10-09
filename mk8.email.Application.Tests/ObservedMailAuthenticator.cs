using mk8.email.Application.Interfaces;

namespace mk8.email.Application.Tests;

internal sealed class ObservedMailAuthenticator(
    IMailAuthenticator inner, NativeAuthenticationObserver observer) : IMailAuthenticator
{
    public Task<AuthenticatedMailUser?> AuthenticateAsync(
        string username, string password, CancellationToken cancellationToken = default) =>
        ObserveAsync(username, password, primary: false, cancellationToken);

    public Task<AuthenticatedMailUser?> AuthenticatePrimaryAsync(
        string username, string password, CancellationToken cancellationToken = default) =>
        ObserveAsync(username, password, primary: true, cancellationToken);

    private async Task<AuthenticatedMailUser?> ObserveAsync(
        string username, string password, bool primary, CancellationToken cancellationToken)
    {
        var span = observer.Enter(primary);
        try
        {
            var result = await (primary
                ? inner.AuthenticatePrimaryAsync(username, password, cancellationToken)
                : inner.AuthenticateAsync(username, password, cancellationToken)).ConfigureAwait(false);
            observer.Record(span, primary, AuthenticationPhase.Returned);
            return result;
        }
        catch (OperationCanceledException)
        {
            observer.Record(span, primary, AuthenticationPhase.Cancelled);
            throw;
        }
        catch
        {
            observer.Record(span, primary, AuthenticationPhase.Faulted);
            throw;
        }
    }
}
