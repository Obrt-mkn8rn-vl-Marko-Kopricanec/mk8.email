using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class InboxService(EmailDbContext db) : IInboxService
{
    public async Task<InboxDTO?> CreateInboxAsync(Guid userId, CreateInboxRequestDTO request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId).ConfigureAwait(false);
        if (user is null)
            return null;

        var address = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AddressId).ConfigureAwait(false);
        if (address is null)
            return null;

        var role = Enum.Parse<UserRole>(user.Role);
        var targetOwnerId = request.ForUserId ?? userId;

        switch (role)
        {
            case UserRole.SuperAdmin:
                break;

            case UserRole.CompanyAdmin:
                if (user.CompanyId is null || address.CompanyId != user.CompanyId)
                    return null;
                break;

            case UserRole.User:
                if (request.ForUserId is not null && request.ForUserId != userId)
                    return null;
                if (user.CompanyId is null || address.CompanyId != user.CompanyId)
                    return null;
                if (await db.Inboxes.CountAsync(i => i.OwnerId == userId).ConfigureAwait(false) >= 1)
                    return null;
                break;

            default:
                return null;
        }

        var companyLimits = await db.CompanyLimits.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CompanyId == address.CompanyId).ConfigureAwait(false);
        var globalLimits = await db.GlobalLimits.AsNoTracking().SingleAsync().ConfigureAwait(false);

        var maxPerCompany = companyLimits?.MaxInboxes ?? globalLimits.DefaultMaxInboxesPerCompany;
        if (maxPerCompany > 0 &&
            await db.Inboxes.CountAsync(i => i.Address.CompanyId == address.CompanyId).ConfigureAwait(false) >= maxPerCompany)
            return null;

        var maxPerDomain = companyLimits?.MaxInboxesPerDomain ?? globalLimits.DefaultMaxInboxesPerDomain;
        if (maxPerDomain > 0 &&
            await db.Inboxes.CountAsync(i => i.AddressId == request.AddressId).ConfigureAwait(false) >= maxPerDomain)
            return null;

        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            AddressId = request.AddressId,
            OwnerId = targetOwnerId,
            AliasForInboxId = request.AliasForInboxId,
        };

        await db.Inboxes.AddAsync(inbox).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (request.AliasForInboxId is null)
        {
            foreach (var folder in DefaultFolders.All)
            {
                await db.Folders.AddAsync(new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = folder,
                    InboxId = inbox.Id,
                }).ConfigureAwait(false);
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new InboxDTO(
            inbox.Id, inbox.Name, inbox.AddressId, address.Domain,
            inbox.OwnerId, inbox.AliasForInboxId, inbox.CreatedAt);
    }

    public async Task<IReadOnlyList<InboxDTO>> GetUserInboxesAsync(Guid userId)
    {
        return await db.Inboxes
            .AsNoTracking()
            .Include(i => i.Address)
            .Where(i => i.OwnerId == userId)
            .Select(i => new InboxDTO(
                i.Id, i.Name, i.AddressId, i.Address.Domain,
                i.OwnerId, i.AliasForInboxId, i.CreatedAt))
            .ToListAsync().ConfigureAwait(false);
    }
}
