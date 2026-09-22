using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Pages.Accounts;

[Authorize(Roles = nameof(UserRole.SuperAdmin))]
public sealed class AccountsModel(
    IMailAdministrationService administration,
    IAdminAuditLog auditLog) : PageModel
{
    public IReadOnlyList<MailAccountSummaryDTO> Accounts { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Accounts = await administration.GetAccountsAsync(cancellationToken);

    public async Task<IActionResult> OnPostSetActiveAsync(
        Guid userId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!isActive && string.Equals(currentUserId, userId.ToString(), StringComparison.Ordinal))
        {
            StatusMessage = "You cannot disable your current account.";
            return RedirectToPage();
        }

        var result = await administration.SetAccountActiveAsync(userId, isActive, cancellationToken);
        await auditLog.WriteAsync(
            User.Identity?.Name ?? "unknown",
            isActive ? "account.enable" : "account.disable",
            userId.ToString(),
            result.Succeeded,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        StatusMessage = result.Message;
        return RedirectToPage();
    }
}
