using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Sieve;

namespace mk8.email.Application.Services;

internal sealed class SieveApplicationService(
    IMailAuthenticator authenticator,
    IOAuthTokenService oauthTokens,
    ISieveScriptService scripts) : ISieveApplicationService
{
    public async Task<SieveIdentityResult> AuthenticatePasswordAsync(
        SievePasswordAuthentication request,
        CancellationToken cancellationToken = default)
    {
        var user = await authenticator.AuthenticateAsync(
            request.Username, request.Password, cancellationToken);
        return new SieveIdentityResult(user?.Id, user?.Username);
    }

    public async Task<SieveIdentityResult> AuthenticateOAuthAsync(
        SieveOAuthAuthentication request,
        CancellationToken cancellationToken = default)
    {
        var user = await oauthTokens.AuthenticateAccessTokenAsync(
            request.AccessToken, "sieve", cancellationToken);
        return new SieveIdentityResult(user?.Id, user?.Username);
    }

    public Task<SieveScriptOperationResult> CheckSpaceAsync(
        SieveCheckSpaceRequest request,
        CancellationToken cancellationToken = default) =>
        scripts.CheckSpaceAsync(
            request.UserId,
            request.Name,
            request.ContentSizeBytes,
            request.MaximumScripts,
            cancellationToken);

    public Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
        SieveUserRequest request,
        CancellationToken cancellationToken = default) =>
        scripts.ListAsync(request.UserId, cancellationToken);

    public async Task<SieveStoredScriptResult> GetAsync(
        SieveNamedRequest request,
        CancellationToken cancellationToken = default) =>
        new(await scripts.GetAsync(request.UserId, request.Name, cancellationToken));

    public Task<SieveScriptOperationResult> PutAsync(
        SievePutRequest request,
        CancellationToken cancellationToken = default) =>
        scripts.PutAsync(
            request.UserId,
            request.Name,
            request.Content,
            request.MaximumScripts,
            cancellationToken);

    public Task<SieveScriptOperationResult> SetActiveAsync(
        SieveSetActiveRequest request,
        CancellationToken cancellationToken = default) =>
        scripts.SetActiveAsync(request.UserId, request.Name, cancellationToken);

    public Task<SieveScriptOperationResult> DeleteAsync(
        SieveNamedRequest request,
        CancellationToken cancellationToken = default) =>
        scripts.DeleteAsync(request.UserId, request.Name, cancellationToken);

    public Task<SieveScriptOperationResult> RenameAsync(
        SieveRenameRequest request,
        CancellationToken cancellationToken = default) =>
        scripts.RenameAsync(
            request.UserId,
            request.OldName,
            request.NewName,
            cancellationToken);

    public Task<SieveValidationResult> ValidateAsync(
        SieveValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilation = scripts.Validate(request.Content);
        var first = compilation.Diagnostics.FirstOrDefault();
        return Task.FromResult(new SieveValidationResult(
            compilation.Succeeded,
            first is null
                ? null
                : new SieveDiagnosticResult(first.Line, first.Column, first.Message)));
    }
}
