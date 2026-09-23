namespace mk8.email.Contracts.DTOs;

public sealed record AdminDashboardDataDTO(
    IReadOnlyList<MailDomainSummaryDTO> Domains,
    IReadOnlyList<MailAccountSummaryDTO> Accounts);

public sealed record AdminDashboardDTO(
    IReadOnlyList<MailDomainSummaryDTO> Domains,
    IReadOnlyList<MailAccountSummaryDTO> Accounts,
    MailSystemStatusDTO SystemStatus);
