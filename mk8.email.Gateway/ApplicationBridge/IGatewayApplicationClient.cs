using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.ApplicationBridge;

public interface IGatewayApplicationClient
{
    Task<LoginResultDTO> AuthenticateAsync(
        LoginRequestDTO request,
        CancellationToken cancellationToken = default);

    Task<AdminDashboardDTO> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailDomainSummaryDTO>> GetDomainsAsync(
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> EnsureDomainAsync(
        AdminEnsureDomainRequest request,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> SetCatchAllAsync(
        AdminSetCatchAllRequest request,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> SetDomainActiveAsync(
        AdminSetDomainActiveRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailAccountSummaryDTO>> GetAccountsAsync(
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> CreateAccountAsync(
        AdminCreateAccountRequest request,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> SetAccountActiveAsync(
        AdminSetAccountActiveRequest request,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> ResetPasswordAsync(
        AdminResetPasswordRequest request,
        CancellationToken cancellationToken = default);
}
