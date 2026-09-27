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

        var targetOwnerId = request.ForUserId ?? userId;
        if (!await CanCreateInboxAsync(user, address, request).ConfigureAwait(false))
            return null;
        if (!await HasInboxCapacityAsync(address).ConfigureAwait(false))
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

    private async Task<bool> CanCreateInboxAsync(UserDB user, AddressDB address, CreateInboxRequestDTO request)
    {
        switch (Enum.Parse<UserRole>(user.Role))
        {
            case UserRole.SuperAdmin:
                return true;

            case UserRole.CompanyAdmin:
                return user.CompanyId is not null && address.CompanyId == user.CompanyId;

            case UserRole.User:
                if (request.ForUserId is not null && request.ForUserId != user.Id)
                    return false;
                if (user.CompanyId is null || address.CompanyId != user.CompanyId)
                    return false;
                return await db.Inboxes.CountAsync(i => i.OwnerId == user.Id).ConfigureAwait(false) < 1;

            default:
                return false;
        }
    }

    private async Task<bool> HasInboxCapacityAsync(AddressDB address)
    {
        var companyLimits = await db.CompanyLimits.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CompanyId == address.CompanyId).ConfigureAwait(false);
        var globalLimits = await db.GlobalLimits.AsNoTracking().SingleAsync().ConfigureAwait(false);
        var maxPerCompany = companyLimits?.MaxInboxes ?? globalLimits.DefaultMaxInboxesPerCompany;
        if (maxPerCompany > 0 &&
            await db.Inboxes.CountAsync(i => i.Address.CompanyId == address.CompanyId).ConfigureAwait(false) >= maxPerCompany)
            return false;
        var maxPerDomain = companyLimits?.MaxInboxesPerDomain ?? globalLimits.DefaultMaxInboxesPerDomain;
        return maxPerDomain <= 0 ||
            await db.Inboxes.CountAsync(i => i.AddressId == address.Id).ConfigureAwait(false) < maxPerDomain;
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
