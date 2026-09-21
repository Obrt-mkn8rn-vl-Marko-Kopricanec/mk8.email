using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public sealed class ApplicationPasswordService(EmailDbContext database) : IApplicationPasswordService
{
    private const int MaximumActivePasswordsPerUser = 50;
    private const int MaximumNameLength = 128;

    public async Task<ApplicationPasswordCreationResult> CreateAsync(
        string username,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return Failure("The account address is not valid.");
        if (normalizedName.Length is < 1 or > MaximumNameLength)
            return Failure($"The application password name must contain from 1 through {MaximumNameLength} characters.");

        var user = await FindActiveUserAsync(normalized, cancellationToken);
        if (user is null)
            return Failure("The account does not exist or is not active.");

        var activeCount = await database.ApplicationPasswords.CountAsync(
            password => password.UserId == user.Id && password.RevokedAt == null,
            cancellationToken);
        if (activeCount >= MaximumActivePasswordsPerUser)
            return Failure($"The account already has {MaximumActivePasswordsPerUser} active application passwords.");

        var id = Guid.CreateVersion7();
        var secretBytes = RandomNumberGenerator.GetBytes(20);
        var secret = Convert.ToBase64String(secretBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var passwordValue = $"mk8_{id:N}_{secret}";
        var now = DateTime.UtcNow;
        database.ApplicationPasswords.Add(new ApplicationPasswordDB
        {
            Id = id,
            UserId = user.Id,
            Name = normalizedName,
            PasswordHash = PasswordHasher.Hash(passwordValue),
            CreatedAt = now,
        });
        await database.SaveChangesAsync(cancellationToken);

        return new(
            true,
            "The application password was created. It will not be shown again.",
            id,
            passwordValue);
    }

    public async Task<IReadOnlyList<ApplicationPasswordSummary>> ListAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        if (normalized.Length == 0)
            return [];

        return await database.ApplicationPasswords
            .AsNoTracking()
            .Where(password => password.User.Username == normalized)
            .OrderByDescending(password => password.CreatedAt)
            .Select(password => new ApplicationPasswordSummary(
                password.Id,
                password.Name,
                password.CreatedAt,
                password.LastUsedAt,
                password.RevokedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(
        string username,
        Guid applicationPasswordId,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        if (normalized.Length == 0)
            return false;

        var password = await database.ApplicationPasswords
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == applicationPasswordId
                    && candidate.User.Username == normalized,
                cancellationToken);
        if (password is null)
            return false;
        if (password.RevokedAt is null)
        {
            password.RevokedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private async Task<UserDB?> FindActiveUserAsync(
        string username,
        CancellationToken cancellationToken)
    {
        var separator = username.LastIndexOf('@');
        var domain = username[(separator + 1)..];
        return await database.Users
            .SingleOrDefaultAsync(user => user.Username == username
                && user.IsActive
                && user.CompanyId != null
                && user.Company != null
                && user.Company.IsActive
                && database.Addresses.Any(address =>
                    address.CompanyId == user.CompanyId
                    && address.Domain == domain
                    && address.IsActive),
                cancellationToken);
    }

    private static string NormalizeUsername(string username) =>
        SmtpAddress.TryNormalize(username, allowEmpty: false, out var normalized)
            ? normalized
            : string.Empty;

    private static ApplicationPasswordCreationResult Failure(string message) =>
        new(false, message);
}
