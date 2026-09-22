using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Configuration;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(
    IGatewayApplicationClient application,
    IAdminAuditLog auditLog,
    AdminConfig config) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await application.AuthenticateAsync(
            new LoginRequestDTO(Input.Username, Input.Password),
            cancellationToken);
        var isAdministrator = result.Success
            && result.User is not null
            && result.User.Role == UserRole.SuperAdmin;

        await auditLog.WriteAsync(
            Input.Username,
            "administrator.login",
            Input.Username,
            isAdministrator,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        if (!isAdministrator)
        {
            ModelState.AddModelError(string.Empty, "The username or password is not valid.");
            return Page();
        }

        var user = result.User!;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties
        {
            AllowRefresh = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(config.SessionMinutes),
            IsPersistent = false,
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            properties);

        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
    }

    public sealed class LoginInput
    {
        [Required]
        [Display(Name = "Email address")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
