using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class CompanyService(EmailDbContext db) : ICompanyService
{
    public async Task<CompanyDTO?> CreateCompanyAsync(Guid userId, CreateCompanyRequestDTO request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await IsSuperAdminAsync(userId).ConfigureAwait(false))
            return null;

        if (await db.Companies.AnyAsync(c => c.Name == request.Name).ConfigureAwait(false))
            return null;

        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
        };

        await db.Companies.AddAsync(company).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return ToDTO(company);
    }

    public async Task<IReadOnlyList<CompanyDTO>> GetAllCompaniesAsync()
    {
        return await db.Companies.AsNoTracking()
            .Select(c => new CompanyDTO(c.Id, c.Name, c.IsActive, c.CreatedAt))
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<CompanyDTO?> GetCompanyAsync(Guid companyId)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId).ConfigureAwait(false);
        return company is null ? null : ToDTO(company);
    }

    public async Task<GlobalConfigDTO> GetGlobalConfigAsync()
    {
        // The table has a seeded row but no constraint limiting its total row count.
#pragma warning disable HLQ005
        var c = await db.GlobalConfig.SingleAsync().ConfigureAwait(false);
#pragma warning restore HLQ005
        return ToConfigDTO(c);
    }

    public async Task<GlobalConfigDTO?> UpdateGlobalConfigAsync(Guid userId, GlobalConfigDTO config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!await IsSuperAdminAsync(userId).ConfigureAwait(false))
            return null;

        // Do not silently modify an arbitrary row if the singleton table is corrupt.
#pragma warning disable HLQ005
        var e = await db.GlobalConfig.SingleAsync().ConfigureAwait(false);
#pragma warning restore HLQ005

        e.AllowRegistration = config.AllowRegistration;

        e.SmtpHostname = config.SmtpHostname;
        e.SmtpPort = config.SmtpPort;
        e.SmtpSubmissionPort = config.SmtpSubmissionPort;
        e.SmtpImplicitTlsPort = config.SmtpImplicitTlsPort;

        e.EnableSmtp = config.EnableSmtp;
        e.EnableSubmission = config.EnableSubmission;
        e.EnableImplicitTls = config.EnableImplicitTls;

        e.EnableStartTls = config.EnableStartTls;
        e.RequireTls = config.RequireTls;
        e.TlsCertificatePath = config.TlsCertificatePath;
        e.TlsCertificateKeyPath = config.TlsCertificateKeyPath;

        e.PasswordHashScheme = config.PasswordHashScheme;
        e.RequireAuth = config.RequireAuth;

        e.MaxMessageSizeBytes = config.MaxMessageSizeBytes;
        e.MaxRecipientsPerMessage = config.MaxRecipientsPerMessage;
        e.ConnectionTimeoutSeconds = config.ConnectionTimeoutSeconds;
        e.MaxConnectionsPerIp = config.MaxConnectionsPerIp;

        e.AllowRelay = config.AllowRelay;

        e.EnableImap = config.EnableImap;
        e.ImapPort = config.ImapPort;
        e.EnableImapImplicitTls = config.EnableImapImplicitTls;
        e.ImapImplicitTlsPort = config.ImapImplicitTlsPort;

        e.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);

        return ToConfigDTO(e);
    }

    public async Task<GlobalLimitsDTO> GetGlobalLimitsAsync()
    {
#pragma warning disable HLQ005 // The limits table has no one-row database constraint.
        var l = await db.GlobalLimits.SingleAsync().ConfigureAwait(false);
#pragma warning restore HLQ005
        return new GlobalLimitsDTO(l.Id, l.DefaultMaxDomainsPerCompany, l.DefaultMaxInboxesPerCompany, l.DefaultMaxInboxesPerDomain);
    }

    public async Task<GlobalLimitsDTO?> UpdateGlobalLimitsAsync(Guid userId, GlobalLimitsDTO limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!await IsSuperAdminAsync(userId).ConfigureAwait(false))
            return null;

#pragma warning disable HLQ005 // A duplicate limits row must fail before mutation.
        var entity = await db.GlobalLimits.SingleAsync().ConfigureAwait(false);
#pragma warning restore HLQ005
        entity.DefaultMaxDomainsPerCompany = limits.DefaultMaxDomainsPerCompany;
        entity.DefaultMaxInboxesPerCompany = limits.DefaultMaxInboxesPerCompany;
        entity.DefaultMaxInboxesPerDomain = limits.DefaultMaxInboxesPerDomain;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new GlobalLimitsDTO(entity.Id, entity.DefaultMaxDomainsPerCompany, entity.DefaultMaxInboxesPerCompany, entity.DefaultMaxInboxesPerDomain);
    }

    public async Task<CompanyConfigDTO?> GetCompanyConfigAsync(Guid userId, Guid companyId)
    {
        if (!await HasCompanyAccessAsync(userId, companyId).ConfigureAwait(false))
            return null;

        var config = await db.CompanyConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId).ConfigureAwait(false);

        return config is null ? null : new CompanyConfigDTO(config.Id, config.CompanyId, config.AllowUserRegistration);
    }

    public async Task<CompanyConfigDTO?> UpdateCompanyConfigAsync(Guid userId, Guid companyId, bool allowUserRegistration)
    {
        if (!await HasCompanyAccessAsync(userId, companyId).ConfigureAwait(false))
            return null;

        var config = await db.CompanyConfigs.FirstOrDefaultAsync(c => c.CompanyId == companyId).ConfigureAwait(false);

        if (config is null)
        {
            config = new CompanyConfigDB
            {
                Id = Guid.CreateVersion7(),
                CompanyId = companyId,
                AllowUserRegistration = allowUserRegistration,
            };
            await db.CompanyConfigs.AddAsync(config).ConfigureAwait(false);
        }
        else
        {
            config.AllowUserRegistration = allowUserRegistration;
            config.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new CompanyConfigDTO(config.Id, config.CompanyId, config.AllowUserRegistration);
    }

    public async Task<CompanyLimitsDTO?> GetCompanyLimitsAsync(Guid userId, Guid companyId)
    {
        if (!await HasCompanyAccessAsync(userId, companyId).ConfigureAwait(false))
            return null;

        var limits = await db.CompanyLimits.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CompanyId == companyId).ConfigureAwait(false);

        return limits is null ? null : new CompanyLimitsDTO(limits.Id, limits.CompanyId, limits.MaxDomains, limits.MaxInboxes, limits.MaxInboxesPerDomain);
    }

    public async Task<CompanyLimitsDTO?> UpdateCompanyLimitsAsync(Guid userId, Guid companyId, int? maxDomains, int? maxInboxes, int? maxInboxesPerDomain)
    {
        if (!await IsSuperAdminAsync(userId).ConfigureAwait(false))
            return null;

        var limits = await db.CompanyLimits.FirstOrDefaultAsync(l => l.CompanyId == companyId).ConfigureAwait(false);

        if (limits is null)
        {
            limits = new CompanyLimitsDB
            {
                Id = Guid.CreateVersion7(),
                CompanyId = companyId,
                MaxDomains = maxDomains,
                MaxInboxes = maxInboxes,
                MaxInboxesPerDomain = maxInboxesPerDomain,
            };
            await db.CompanyLimits.AddAsync(limits).ConfigureAwait(false);
        }
        else
        {
            limits.MaxDomains = maxDomains;
            limits.MaxInboxes = maxInboxes;
            limits.MaxInboxesPerDomain = maxInboxesPerDomain;
            limits.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new CompanyLimitsDTO(limits.Id, limits.CompanyId, limits.MaxDomains, limits.MaxInboxes, limits.MaxInboxesPerDomain);
    }

    private async Task<bool> IsSuperAdminAsync(Guid userId)
    {
        return await db.Users.AnyAsync(u => u.Id == userId && u.Role == nameof(UserRole.SuperAdmin)).ConfigureAwait(false);
    }

    private async Task<bool> HasCompanyAccessAsync(Guid userId, Guid companyId)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId).ConfigureAwait(false);
        if (user is null)
            return false;

        var role = Enum.Parse<UserRole>(user.Role);
        return role switch
        {
            UserRole.SuperAdmin => true,
            UserRole.CompanyAdmin => user.CompanyId == companyId,
            _ => false,
        };
    }

    private static CompanyDTO ToDTO(CompanyDB c) => new(c.Id, c.Name, c.IsActive, c.CreatedAt);

    private static GlobalConfigDTO ToConfigDTO(GlobalConfigDB c) => new(
        c.Id, c.AllowRegistration,
        c.SmtpHostname, c.SmtpPort, c.SmtpSubmissionPort, c.SmtpImplicitTlsPort,
        c.EnableSmtp, c.EnableSubmission, c.EnableImplicitTls,
        c.EnableStartTls, c.RequireTls, c.TlsCertificatePath, c.TlsCertificateKeyPath,
        c.PasswordHashScheme, c.RequireAuth,
        c.MaxMessageSizeBytes, c.MaxRecipientsPerMessage, c.ConnectionTimeoutSeconds, c.MaxConnectionsPerIp,
        c.AllowRelay,
        c.EnableImap, c.ImapPort, c.EnableImapImplicitTls, c.ImapImplicitTlsPort);
}
