using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public sealed partial class MailAdministrationService(EmailDbContext db) : IMailAdministrationService
{
    private const int MinimumPasswordLength = 16;

    public async Task<AdministrationResult> EnsureDomainAsync(
        string companyName,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var normalizedCompany = companyName.Trim();
        var normalizedDomain = NormalizeDomain(domain);
        if (normalizedCompany.Length is < 1 or > 255)
            return Failure("The company name must contain from 1 through 255 characters.");
        if (normalizedDomain is null)
            return Failure("The domain name is not valid.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var company = await db.Companies
            .FirstOrDefaultAsync(item => item.Name == normalizedCompany, cancellationToken);
        if (company is null)
        {
            company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = normalizedCompany,
            };
            db.Companies.Add(company);
        }
        else if (!company.IsActive)
        {
            return Failure("The company is not active.");
        }

        var existingDomain = await db.Addresses
            .FirstOrDefaultAsync(item => item.Domain == normalizedDomain, cancellationToken);
        if (existingDomain is not null)
        {
            if (existingDomain.CompanyId != company.Id)
                return Failure("A different company owns the domain.");

            await transaction.CommitAsync(cancellationToken);
            return Success("The domain already exists.", existingDomain.Id);
        }

        var mailDomain = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = normalizedDomain,
            Company = company,
            CompanyId = company.Id,
            IsActive = false,
        };

        db.Addresses.Add(mailDomain);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Success("The domain was created.", mailDomain.Id);
    }

    public async Task<AdministrationResult> CreateAccountAsync(
        string address,
        string password,
        UserRole role,
        CancellationToken cancellationToken = default)
    {
        var parsedAddress = ParseAddress(address);
        if (parsedAddress is null)
            return Failure("The email address is not valid.");
        if (!IsPasswordValid(password))
            return Failure("The password must contain at least 16 characters.");

        var (localPart, domain) = parsedAddress.Value;
        var normalizedAddress = $"{localPart}@{domain}";
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var mailDomain = await db.Addresses
            .Include(item => item.Company)
            .FirstOrDefaultAsync(
                item => item.Domain == domain && item.Company.IsActive,
                cancellationToken);
        if (mailDomain is null)
            return Failure("The domain does not exist or its company is not active.");

        if (await db.Users.AnyAsync(item => item.Username == normalizedAddress, cancellationToken)
            || await db.Inboxes.AnyAsync(
                item => item.AddressId == mailDomain.Id && item.Name == localPart,
                cancellationToken))
        {
            return Failure("The account already exists.");
        }

        var now = DateTime.UtcNow;
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = normalizedAddress,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role.ToString(),
            CompanyId = mailDomain.CompanyId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = localPart,
            AddressId = mailDomain.Id,
            OwnerId = user.Id,
            CreatedAt = now,
        };

        db.Users.Add(user);
        db.Inboxes.Add(inbox);
        AddDefaultFolders(inbox, now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Success("The account was created.", user.Id);
    }

    public async Task<AdministrationResult> SetCatchAllAsync(
        string domain,
        string targetAddress,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var parsedTarget = ParseAddress(targetAddress);
        if (normalizedDomain is null || parsedTarget is null || parsedTarget.Value.Domain != normalizedDomain)
            return Failure("The catch-all target must use the selected domain.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var target = await db.Inboxes
            .Include(item => item.Address)
            .Include(item => item.Owner)
            .FirstOrDefaultAsync(
                item => item.Address.Domain == normalizedDomain
                    && item.Name == parsedTarget.Value.LocalPart
                    && item.AliasForInboxId == null
                    && item.Address.Company.IsActive
                    && item.Owner.IsActive,
                cancellationToken);
        if (target is null)
            return Failure("The catch-all target account does not exist.");

        var catchAll = await db.Inboxes
            .FirstOrDefaultAsync(
                item => item.AddressId == target.AddressId && item.Name == "*",
                cancellationToken);
        if (catchAll is null)
        {
            catchAll = new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "*",
                AddressId = target.AddressId,
                OwnerId = target.OwnerId,
                AliasForInboxId = target.Id,
            };
            db.Inboxes.Add(catchAll);
        }
        else
        {
            catchAll.OwnerId = target.OwnerId;
            catchAll.AliasForInboxId = target.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Success("The catch-all target was set.", catchAll.Id);
    }

    public async Task<AdministrationResult> SetDomainActiveAsync(
        string domain,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = NormalizeDomain(domain);
        if (normalizedDomain is null)
            return Failure("The domain name is not valid.");

        var mailDomain = await db.Addresses
            .Include(item => item.Company)
            .FirstOrDefaultAsync(item => item.Domain == normalizedDomain, cancellationToken);
        if (mailDomain is null)
            return Failure("The domain does not exist.");
        if (isActive && !mailDomain.Company.IsActive)
            return Failure("The company is not active.");
        if (mailDomain.IsActive == isActive)
            return Success(isActive ? "The domain is already active." : "The domain is already inactive.", mailDomain.Id);

        mailDomain.IsActive = isActive;
        mailDomain.UpdatedAt = DateTime.UtcNow;
        if (!isActive)
        {
            var usernameSuffix = $"@{normalizedDomain}";
            var affectedUserIds = await db.Users
                .Where(user => user.CompanyId == mailDomain.CompanyId
                    && user.Username.EndsWith(usernameSuffix))
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken);
            await RevokePushSubscriptionsAsync(affectedUserIds, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        return Success(isActive ? "The domain was activated." : "The domain was deactivated.", mailDomain.Id);
    }

    public async Task<AdministrationResult> SetAccountActiveAsync(
        Guid userId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
            return Failure("The account does not exist.");

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        if (!isActive)
            await RevokePushSubscriptionsAsync(user.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Success(isActive ? "The account was enabled." : "The account was disabled.", user.Id);
    }

    public async Task<AdministrationResult> ResetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsPasswordValid(password))
            return Failure("The password must contain at least 16 characters.");

        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
            return Failure("The account does not exist.");

        user.PasswordHash = PasswordHasher.Hash(password);
        user.UpdatedAt = DateTime.UtcNow;
        await RevokePushSubscriptionsAsync(user.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Success("The password was changed.", user.Id);
    }

    private async Task RevokePushSubscriptionsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await RevokePushSubscriptionsAsync([userId], cancellationToken);

    private async Task RevokePushSubscriptionsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return;

        var subscriptions = await db.JmapPushSubscriptions
            .Where(subscription => userIds.Contains(subscription.UserId))
            .ToListAsync(cancellationToken);
        foreach (var subscription in subscriptions)
        {
            subscription.Url = string.Empty;
            subscription.KeysJson = null;
        }
        db.JmapPushSubscriptions.RemoveRange(subscriptions);
    }

    public async Task<IReadOnlyList<MailDomainSummaryDTO>> GetDomainsAsync(
        CancellationToken cancellationToken = default)
    {
        var domains = await db.Addresses
            .AsNoTracking()
            .Include(item => item.Company)
            .Include(item => item.Inboxes)
            .ThenInclude(item => item.AliasForInbox)
            .OrderBy(item => item.Domain)
            .ToListAsync(cancellationToken);

        return domains.Select(item =>
        {
            var catchAll = item.Inboxes.FirstOrDefault(inbox => inbox.Name == "*");
            var target = catchAll?.AliasForInbox is null
                ? null
                : $"{catchAll.AliasForInbox.Name}@{item.Domain}";

            return new MailDomainSummaryDTO(
                item.Id,
                item.Domain,
                item.Company.Name,
                item.IsActive,
                item.Inboxes.Count(inbox => inbox.Name != "*" && inbox.AliasForInboxId is null),
                target);
        }).ToList();
    }

    public async Task<IReadOnlyList<MailAccountSummaryDTO>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        var catchAllTargets = await db.Inboxes
            .AsNoTracking()
            .Where(item => item.Name == "*" && item.AliasForInboxId != null)
            .Select(item => item.AliasForInboxId!.Value)
            .ToListAsync(cancellationToken);

        var targetIds = catchAllTargets.ToHashSet();
        var accounts = await db.Inboxes
            .AsNoTracking()
            .Include(item => item.Address)
            .Include(item => item.Owner)
            .Where(item => item.Name != "*" && item.AliasForInboxId == null)
            .OrderBy(item => item.Address.Domain)
            .ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);

        return accounts.Select(item => new MailAccountSummaryDTO(
            item.OwnerId,
            $"{item.Name}@{item.Address.Domain}",
            Enum.Parse<UserRole>(item.Owner.Role),
            item.Owner.IsActive,
            item.Address.IsActive,
            targetIds.Contains(item.Id),
            item.Owner.CreatedAt)).ToList();
    }

    private static AdministrationResult Failure(string message) => new(false, message);

    private static AdministrationResult Success(string message, Guid entityId) => new(true, message, entityId);

    private static bool IsPasswordValid(string password) =>
        !string.IsNullOrWhiteSpace(password) && password.Length >= MinimumPasswordLength;

    private static void AddDefaultFolders(InboxDB inbox, DateTime now)
    {
        foreach (var name in DefaultFolders.All)
        {
            inbox.Folders.Add(new FolderDB
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                InboxId = inbox.Id,
                CreatedAt = now,
            });
        }
    }

    private static (string LocalPart, string Domain)? ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var separator = address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Length - 1)
            return null;

        var localPart = address[..separator].Trim().ToLowerInvariant();
        var domain = NormalizeDomain(address[(separator + 1)..]);
        if (domain is null
            || localPart.Length > 64
            || !LocalPartPattern().IsMatch(localPart)
            || localPart.StartsWith(".", StringComparison.Ordinal)
            || localPart.EndsWith(".", StringComparison.Ordinal)
            || localPart.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return (localPart, domain);
    }

    private static string? NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        string asciiDomain;
        try
        {
            asciiDomain = new IdnMapping().GetAscii(domain.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        return asciiDomain.Length <= 253
            && Uri.CheckHostName(asciiDomain) == UriHostNameType.Dns
            && asciiDomain.Contains('.', StringComparison.Ordinal)
                ? asciiDomain
                : null;
    }

    [GeneratedRegex("^[a-z0-9!#$%&'*+/=?^_`{|}~.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPartPattern();
}
