using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.DTOs;

namespace mk8.email.Gateway.Pages;

[Authorize(Roles = nameof(UserRole.SuperAdmin))]
public sealed class IndexModel(
    IMailAdministrationService administration,
    IMailSystemStatusService systemStatus) : PageModel
{
    public int DomainCount { get; private set; }
    public int ActiveAccountCount { get; private set; }
    public int CatchAllCount { get; private set; }
    public MailSystemStatusDTO SystemStatus { get; private set; } = MailSystemStatusDTO.Unavailable;
    public string OperationalState { get; private set; } = "Unavailable";
    public string OperationalClass { get; private set; } = "status-bad";
    public string LastCheck { get; private set; } = "Unavailable";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var domains = await administration.GetDomainsAsync(cancellationToken);
        var accounts = await administration.GetAccountsAsync(cancellationToken);
        SystemStatus = await systemStatus.GetStatusAsync(cancellationToken);
        DomainCount = domains.Count(domain => domain.IsActive);
        ActiveAccountCount = accounts.Count(account => account.IsActive && account.IsDomainActive);
        CatchAllCount = domains.Count(domain => domain.CatchAllTarget is not null);

        var current = SystemStatus.CheckedAt is not null
            && SystemStatus.CheckedAt <= DateTimeOffset.UtcNow.AddMinutes(2)
            && SystemStatus.CheckedAt >= DateTimeOffset.UtcNow.AddMinutes(-10);
        if (!current)
        {
            OperationalState = SystemStatus.CheckedAt is null ? "Unavailable" : "Stale";
            OperationalClass = "status-bad";
        }
        else if (SystemStatus.State == "healthy")
        {
            OperationalState = "Healthy";
            OperationalClass = "status-good";
        }
        else
        {
            OperationalState = $"Unhealthy ({SystemStatus.ErrorCount})";
            OperationalClass = "status-bad";
        }

        LastCheck = SystemStatus.CheckedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            ?? "Unavailable";
    }

    public static string FormatAge(long? seconds)
    {
        if (seconds is null)
            return "Unavailable";
        if (seconds < 60)
            return $"{seconds} seconds";
        if (seconds < 3600)
            return $"{seconds / 60} minutes";
        if (seconds < 86400)
            return $"{seconds / 3600} hours";
        return $"{seconds / 86400} days";
    }

    public static string FormatRemaining(long? seconds)
    {
        if (seconds is null)
            return "Unavailable";
        if (seconds <= 0)
            return "Expired";
        return FormatAge(seconds);
    }
}
