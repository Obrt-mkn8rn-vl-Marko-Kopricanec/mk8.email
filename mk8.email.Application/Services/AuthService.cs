using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public class AuthService(EmailDbContext db) : IAuthService
{
    private static readonly string DummyPasswordHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    public async Task<LoginResultDTO> LoginAsync(LoginRequestDTO request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = request.Username.Trim().ToMailLowerInvariant();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive).ConfigureAwait(false);

        var passwordMatches = PasswordHasher.Verify(request.Password, user?.PasswordHash ?? DummyPasswordHash);
        if (user is null || !passwordMatches)
            return new LoginResultDTO(false, null, "Invalid username or password.");

        return new LoginResultDTO(true, ToDTO(user), null);
    }

    public async Task<LoginResultDTO> RegisterAsync(RegisterRequestDTO request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registrationAllowed = await db.GlobalConfig.AsNoTracking()
            .Select(config => config.AllowRegistration)
            .SingleOrDefaultAsync().ConfigureAwait(false);
        if (!registrationAllowed)
            return new LoginResultDTO(false, null, "Registration is disabled.");

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return new LoginResultDTO(false, null, "A username and password are required.");
        if (request.Password.Length < 12)
            return new LoginResultDTO(false, null, "The password must contain at least 12 characters.");

        if (await db.Users.AnyAsync(u => u.Username == request.Username).ConfigureAwait(false))
            return new LoginResultDTO(false, null, "An account with this username already exists.");

        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = request.Username,
            PasswordHash = PasswordHasher.Hash(request.Password),
            Role = nameof(UserRole.User),
        };

        await db.Users.AddAsync(user).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new LoginResultDTO(true, ToDTO(user), null);
    }

    private static UserDTO ToDTO(UserDB u) => new(
        u.Id, u.Username, Enum.Parse<UserRole>(u.Role), u.CompanyId, u.IsActive, u.CreatedAt);
}
