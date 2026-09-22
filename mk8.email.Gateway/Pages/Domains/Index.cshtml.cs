using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Gateway.Security;

namespace mk8.email.Gateway.Pages.Domains;

[Authorize(Roles = nameof(UserRole.SuperAdmin))]
public sealed class DomainsModel(
    IMailAdministrationService administration,
    IAdminAuditLog auditLog) : PageModel
{
    public IReadOnlyList<MailDomainSummaryDTO> Domains { get; private set; } = [];

    [BindProperty]
    public CreateDomainInput CreateInput { get; set; } = new();

    [BindProperty]
    public CatchAllRouteInput CatchAllInput { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Domains = await administration.GetDomainsAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var result = await administration.EnsureDomainAsync(
            CreateInput.CompanyName,
            CreateInput.Domain,
            cancellationToken);
        await WriteAuditAsync("domain.create", CreateInput.Domain, result.Succeeded, cancellationToken);
        StatusMessage = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCatchAllAsync(CancellationToken cancellationToken)
    {
        var result = await administration.SetCatchAllAsync(
            CatchAllInput.Domain,
            CatchAllInput.TargetAddress,
            cancellationToken);
        await WriteAuditAsync("domain.catchall.set", CatchAllInput.Domain, result.Succeeded, cancellationToken);
        StatusMessage = result.Message;
        return RedirectToPage();
    }

    private Task WriteAuditAsync(string action, string target, bool succeeded, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(
            User.Identity?.Name ?? "unknown",
            action,
            target,
            succeeded,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

    public sealed class CreateDomainInput
    {
        [Required]
        [StringLength(255)]
        [Display(Name = "Company name")]
        public string CompanyName { get; set; } = string.Empty;

        [Required]
        [StringLength(253)]
        public string Domain { get; set; } = string.Empty;
    }

    public sealed class CatchAllRouteInput
    {
        [Required]
        [StringLength(253)]
        public string Domain { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [Display(Name = "Target address")]
        public string TargetAddress { get; set; } = string.Empty;
    }
}
