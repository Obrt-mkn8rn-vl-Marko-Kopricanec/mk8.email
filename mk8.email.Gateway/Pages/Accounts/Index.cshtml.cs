using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Pages.Accounts;

[Authorize(Roles = nameof(UserRole.SuperAdmin))]
public sealed class AccountsModel(
    IGatewayApplicationClient application,
    IAdminAuditLog auditLog) : PageModel
{
    public IReadOnlyList<MailAccountSummaryDTO> Accounts { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Accounts = await application.GetAccountsAsync(cancellationToken).ConfigureAwait(false);

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

        var result = await application.SetAccountActiveAsync(
            new AdminSetAccountActiveRequest(userId, isActive),
            cancellationToken).ConfigureAwait(false);
        await auditLog.WriteAsync(
            User.Identity?.Name ?? "unknown",
            isActive ? "account.enable" : "account.disable",
            userId.ToString(),
            result.Succeeded,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);
        StatusMessage = result.Message;
        return RedirectToPage();
    }
}
