namespace mk8.email.Contracts.DTOs;

public sealed record AdminDashboardDTO(
    IReadOnlyList<MailDomainSummaryDTO> Domains,
    IReadOnlyList<MailAccountSummaryDTO> Accounts,
    MailSystemStatusDTO SystemStatus);
