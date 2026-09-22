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
public sealed class CreateAccountModel(
    IGatewayApplicationClient application,
    IAdminAuditLog auditLog) : PageModel
{
    [BindProperty]
    public AccountInput Input { get; set; } = new();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await application.CreateAccountAsync(
            new AdminCreateAccountRequest(Input.Address, Input.Password, Input.Role),
            cancellationToken);
        await auditLog.WriteAsync(
            User.Identity?.Name ?? "unknown",
            "account.create",
            Input.Address,
            result.Succeeded,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return Page();
        }

        TempData["StatusMessage"] = result.Message;
        return RedirectToPage("Index");
    }

    public sealed class AccountInput
    {
        [Required]
        [EmailAddress]
        [StringLength(320)]
        [Display(Name = "Email address")]
        public string Address { get; set; } = string.Empty;

        [Required]
        public UserRole Role { get; set; } = UserRole.User;

        [Required]
        [StringLength(128, MinimumLength = 16)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
