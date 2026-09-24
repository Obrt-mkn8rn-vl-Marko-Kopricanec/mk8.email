using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public sealed class MailAuthenticator(EmailDbContext database) : IMailAuthenticator
{
    private static readonly string DummyPasswordHash =
        PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    public async Task<AuthenticatedMailUser?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        await AuthenticateInternalAsync(
            username,
            password,
            allowApplicationPassword: true,
            cancellationToken).ConfigureAwait(false);

    public async Task<AuthenticatedMailUser?> AuthenticatePrimaryAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        await AuthenticateInternalAsync(
            username,
            password,
            allowApplicationPassword: false,
            cancellationToken).ConfigureAwait(false);

    private async Task<AuthenticatedMailUser?> AuthenticateInternalAsync(
        string username,
        string password,
        bool allowApplicationPassword,
        CancellationToken cancellationToken)
    {
        var normalized = SmtpAddress.TryNormalize(username, allowEmpty: false, out var mailbox)
            ? mailbox
            : string.Empty;
        var separator = normalized.LastIndexOf('@');
        var domain = separator > 0 ? normalized[(separator + 1)..] : string.Empty;

        var candidate = await database.Users
            .AsNoTracking()
            .Where(user => user.Username == normalized
                && user.IsActive
                && user.CompanyId != null
                && user.Company != null
                && user.Company.IsActive
                && database.Addresses.Any(address =>
                    address.CompanyId == user.CompanyId
                    && address.Domain == domain
                    && address.IsActive))
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.PasswordHash,
                HasActiveMfa = database.MfaTotpCredentials.Any(credential =>
                    credential.UserId == user.Id
                    && credential.VerifiedAt != null
                    && credential.RevokedAt == null),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var accountPasswordMatches = PasswordHasher.Verify(
            password,
            candidate?.PasswordHash ?? DummyPasswordHash);
        if (candidate is null)
            return null;
        if (accountPasswordMatches && (!allowApplicationPassword || !candidate.HasActiveMfa))
            return new AuthenticatedMailUser(candidate.Id, candidate.Username);

        if (!allowApplicationPassword
            || !TryParseApplicationPasswordId(password, out var applicationPasswordId))
            return null;

        var applicationPassword = await database.ApplicationPasswords
            .FirstOrDefaultAsync(
                credential => credential.Id == applicationPasswordId
                    && credential.UserId == candidate.Id
                    && credential.RevokedAt == null,
                cancellationToken).ConfigureAwait(false);
        var applicationPasswordMatches = PasswordHasher.Verify(
            password,
            applicationPassword?.PasswordHash ?? DummyPasswordHash);
        if (applicationPassword is null || !applicationPasswordMatches)
            return null;

        applicationPassword.LastUsedAt = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AuthenticatedMailUser(candidate.Id, candidate.Username);
    }

    private static bool TryParseApplicationPasswordId(string password, out Guid id)
    {
        const int identifierStart = 4;
        const int identifierLength = 32;
        const int separatorIndex = identifierStart + identifierLength;
        id = Guid.Empty;
        return password.Length > separatorIndex + 1
            && password.StartsWith("mk8_", StringComparison.Ordinal)
            && password[separatorIndex] == '_'
            && Guid.TryParseExact(
                password.AsSpan(identifierStart, identifierLength),
                "N",
                out id);
    }
}
