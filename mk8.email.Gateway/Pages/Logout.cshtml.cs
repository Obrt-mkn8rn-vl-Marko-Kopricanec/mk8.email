using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Pages;

[Authorize]
public sealed class LogoutModel(IAdminAuditLog auditLog) : PageModel
{
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await auditLog.WriteAsync(
            User.Identity?.Name ?? "unknown",
            "administrator.logout",
            User.Identity?.Name ?? "unknown",
            true,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
