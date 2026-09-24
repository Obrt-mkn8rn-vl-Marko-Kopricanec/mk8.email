using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Pages.Accounts;

[Authorize(Roles = nameof(UserRole.SuperAdmin))]
public sealed class ResetPasswordModel(
    IGatewayApplicationClient application,
    IAdminAuditLog auditLog) : PageModel
{
    public string Address { get; private set; } = string.Empty;

    [BindProperty]
    public PasswordInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = (await application.GetAccountsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.UserId == userId);
        if (account is null)
            return NotFound();

        Address = account.Address;
        Input.UserId = userId;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await application.ResetPasswordAsync(
            new AdminResetPasswordRequest(Input.UserId, Input.Password),
            cancellationToken).ConfigureAwait(false);
        await auditLog.WriteAsync(
            User.Identity?.Name ?? "unknown",
            "account.password.change",
            Input.UserId.ToString(),
            result.Succeeded,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return Page();
        }

        TempData["StatusMessage"] = result.Message;
        return RedirectToPage("Index");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1034", Justification = "The existing nested input type is bound by Razor forms.")]
    public sealed class PasswordInput
    {
        [Required]
        public Guid UserId { get; set; }

        [Required]
        [StringLength(128, MinimumLength = 16)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
