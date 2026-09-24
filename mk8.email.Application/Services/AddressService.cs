using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class AddressService(EmailDbContext db) : IAddressService
{
    public async Task<AddressDTO?> CreateAddressAsync(Guid userId, CreateAddressRequestDTO request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId).ConfigureAwait(false);
        if (user is null)
            return null;

        var role = Enum.Parse<UserRole>(user.Role);
        switch (role)
        {
            case UserRole.SuperAdmin:
                break;

            case UserRole.CompanyAdmin:
                if (user.CompanyId is null || request.CompanyId != user.CompanyId)
                    return null;
                break;

            default:
                return null;
        }

        if (await db.Addresses.AnyAsync(a => a.Domain == request.Domain).ConfigureAwait(false))
            return null;

        var companyLimits = await db.CompanyLimits.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CompanyId == request.CompanyId).ConfigureAwait(false);
        var globalLimits = await db.GlobalLimits.AsNoTracking().SingleAsync().ConfigureAwait(false);

        var maxDomains = companyLimits?.MaxDomains ?? globalLimits.DefaultMaxDomainsPerCompany;
        if (maxDomains > 0 &&
            await db.Addresses.CountAsync(a => a.CompanyId == request.CompanyId).ConfigureAwait(false) >= maxDomains)
            return null;

        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = request.Domain,
            CompanyId = request.CompanyId,
            IsActive = false,
        };

        await db.Addresses.AddAsync(address).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new AddressDTO(address.Id, address.Domain, address.CompanyId, address.IsActive, address.CreatedAt);
    }

    public async Task<IReadOnlyList<AddressDTO>> GetAllAddressesAsync()
    {
        return await db.Addresses
            .AsNoTracking()
            .Select(a => new AddressDTO(a.Id, a.Domain, a.CompanyId, a.IsActive, a.CreatedAt))
            .ToListAsync().ConfigureAwait(false);
    }
}
