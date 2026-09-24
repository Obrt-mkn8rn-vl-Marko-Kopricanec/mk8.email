// Protocol request/result types are deliberately grouped in this transport-contract file.
#pragma warning disable MA0048
namespace mk8.email.Contracts.DTOs;

public sealed record AdminDashboardDataDTO(
    IReadOnlyList<MailDomainSummaryDTO> Domains,
    IReadOnlyList<MailAccountSummaryDTO> Accounts);

public sealed record AdminDashboardDTO(
    IReadOnlyList<MailDomainSummaryDTO> Domains,
    IReadOnlyList<MailAccountSummaryDTO> Accounts,
    MailSystemStatusDTO SystemStatus);
