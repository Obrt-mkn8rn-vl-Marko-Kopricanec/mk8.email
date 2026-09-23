using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.ApplicationBridge;

public sealed class GatewayApplicationClient(
    IGatewayApplicationTransport transport,
    GatewayMailSystemStatusReader statusReader)
    : IGatewayApplicationClient
{
    public Task<LoginResultDTO> AuthenticateAsync(
        LoginRequestDTO request,
        CancellationToken cancellationToken = default) =>
        SendAsync<LoginRequestDTO, LoginResultDTO>(
            ApplicationOperations.AdminAuthenticate,
            request,
            cancellationToken);

    public async Task<AdminDashboardDTO> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<object, AdminDashboardDataDTO>(
            ApplicationOperations.AdminDashboardGet,
            new { },
            cancellationToken);
        var status = await statusReader.GetStatusAsync(cancellationToken);
        return new AdminDashboardDTO(data.Domains, data.Accounts, status);
    }

    public Task<IReadOnlyList<MailDomainSummaryDTO>> GetDomainsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IReadOnlyList<MailDomainSummaryDTO>>(
            ApplicationOperations.AdminDomainsGet,
            new { },
            cancellationToken);

    public Task<AdministrationResult> EnsureDomainAsync(
        AdminEnsureDomainRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminEnsureDomainRequest, AdministrationResult>(
            ApplicationOperations.AdminDomainsEnsure,
            request,
            cancellationToken);

    public Task<AdministrationResult> SetCatchAllAsync(
        AdminSetCatchAllRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminSetCatchAllRequest, AdministrationResult>(
            ApplicationOperations.AdminDomainsSetCatchAll,
            request,
            cancellationToken);

    public Task<AdministrationResult> SetDomainActiveAsync(
        AdminSetDomainActiveRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminSetDomainActiveRequest, AdministrationResult>(
            ApplicationOperations.AdminDomainsSetActive,
            request,
            cancellationToken);

    public Task<IReadOnlyList<MailAccountSummaryDTO>> GetAccountsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IReadOnlyList<MailAccountSummaryDTO>>(
            ApplicationOperations.AdminAccountsGet,
            new { },
            cancellationToken);

    public Task<AdministrationResult> CreateAccountAsync(
        AdminCreateAccountRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminCreateAccountRequest, AdministrationResult>(
            ApplicationOperations.AdminAccountsCreate,
            request,
            cancellationToken);

    public Task<AdministrationResult> SetAccountActiveAsync(
        AdminSetAccountActiveRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminSetAccountActiveRequest, AdministrationResult>(
            ApplicationOperations.AdminAccountsSetActive,
            request,
            cancellationToken);

    public Task<AdministrationResult> ResetPasswordAsync(
        AdminResetPasswordRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminResetPasswordRequest, AdministrationResult>(
            ApplicationOperations.AdminAccountsResetPassword,
            request,
            cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest value,
        CancellationToken cancellationToken) =>
        await transport.SendAsync<TRequest, TResponse>(
            "admin",
            operation,
            value,
            cancellationToken);
}
