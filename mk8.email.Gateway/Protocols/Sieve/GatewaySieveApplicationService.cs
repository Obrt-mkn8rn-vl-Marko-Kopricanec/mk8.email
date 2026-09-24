using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Sieve;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Sieve;

public sealed class GatewaySieveApplicationService(
    IGatewayApplicationTransport transport) : ISieveApplicationService
{
    public Task<SieveIdentityResult> AuthenticatePasswordAsync(
        SievePasswordAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SievePasswordAuthentication, SieveIdentityResult>(
            "sieve", ApplicationOperations.SieveAuthenticatePassword, request, cancellationToken);

    public Task<SieveIdentityResult> AuthenticateOAuthAsync(
        SieveOAuthAuthentication request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveOAuthAuthentication, SieveIdentityResult>(
            "sieve", ApplicationOperations.SieveAuthenticateOAuth, request, cancellationToken);

    public Task<SieveScriptOperationResult> CheckSpaceAsync(
        SieveCheckSpaceRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveCheckSpaceRequest, SieveScriptOperationResult>(
            "sieve", ApplicationOperations.SieveCheckSpace, request, cancellationToken);

    public async Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
        SieveUserRequest request,
        CancellationToken cancellationToken = default) =>
        await transport.SendAsync<SieveUserRequest, List<SieveScriptSummary>>(
            "sieve", ApplicationOperations.SieveList, request, cancellationToken).ConfigureAwait(false);

    public Task<SieveStoredScriptResult> GetAsync(
        SieveNamedRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveNamedRequest, SieveStoredScriptResult>(
            "sieve", ApplicationOperations.SieveGet, request, cancellationToken);

    public Task<SieveScriptOperationResult> PutAsync(
        SievePutRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SievePutRequest, SieveScriptOperationResult>(
            "sieve", ApplicationOperations.SievePut, request, cancellationToken);

    public Task<SieveScriptOperationResult> SetActiveAsync(
        SieveSetActiveRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveSetActiveRequest, SieveScriptOperationResult>(
            "sieve", ApplicationOperations.SieveSetActive, request, cancellationToken);

    public Task<SieveScriptOperationResult> DeleteAsync(
        SieveNamedRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveNamedRequest, SieveScriptOperationResult>(
            "sieve", ApplicationOperations.SieveDelete, request, cancellationToken);

    public Task<SieveScriptOperationResult> RenameAsync(
        SieveRenameRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveRenameRequest, SieveScriptOperationResult>(
            "sieve", ApplicationOperations.SieveRename, request, cancellationToken);

    public Task<SieveValidationResult> ValidateAsync(
        SieveValidationRequest request,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync<SieveValidationRequest, SieveValidationResult>(
            "sieve", ApplicationOperations.SieveValidate, request, cancellationToken);
}
