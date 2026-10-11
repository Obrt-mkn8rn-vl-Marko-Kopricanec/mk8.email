using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public sealed class MailAuthenticator : IMailAuthenticator
{
    private readonly EmailDbContext _database;
    private readonly IMailAuthenticationStageObserver? _observer;
    private static readonly string DummyPasswordHash =
        PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    public MailAuthenticator(EmailDbContext database) : this(database, observer: null) { }

    internal MailAuthenticator(EmailDbContext database, IMailAuthenticationStageObserver? observer)
    {
        _database = database;
        _observer = observer;
    }

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

        var call = _observer is null ? Guid.Empty : Guid.NewGuid();
        var candidate = await ObserveAsync(call, MailAuthenticationStage.CandidateQuery, () => _database.Users
            .AsNoTracking()
            .Where(user => user.Username == normalized
                && user.IsActive
                && user.CompanyId != null
                && user.Company != null
                && user.Company.IsActive
                && _database.Addresses.Any(address =>
                    address.CompanyId == user.CompanyId
                    && address.Domain == domain
                    && address.IsActive))
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.PasswordHash,
                HasActiveMfa = _database.MfaTotpCredentials.Any(credential =>
                    credential.UserId == user.Id
                    && credential.VerifiedAt != null
                    && credential.RevokedAt == null),
            })
            .SingleOrDefaultAsync(cancellationToken)).ConfigureAwait(false);

        var accountPasswordMatches = Observe(call, MailAuthenticationStage.AccountPasswordVerification, () => PasswordHasher.Verify(
            password,
            candidate?.PasswordHash ?? DummyPasswordHash));
        if (candidate is null)
            return null;
        if (accountPasswordMatches && (!allowApplicationPassword || !candidate.HasActiveMfa))
            return new AuthenticatedMailUser(candidate.Id, candidate.Username);

        if (!allowApplicationPassword
            || !TryParseApplicationPasswordId(password, out var applicationPasswordId))
        {
            return null;
        }

        return await AuthenticateApplicationPasswordAsync(
            call, candidate.Id, candidate.Username, password, applicationPasswordId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticatedMailUser?> AuthenticateApplicationPasswordAsync(
        Guid call, Guid userId, string username, string password,
        Guid applicationPasswordId, CancellationToken cancellationToken)
    {
        var applicationPassword = await ObserveAsync(call, MailAuthenticationStage.ApplicationPasswordQuery, () => _database.ApplicationPasswords
            .FirstOrDefaultAsync(
                credential => credential.Id == applicationPasswordId
                    && credential.UserId == userId
                    && credential.RevokedAt == null,
                cancellationToken)).ConfigureAwait(false);
        var applicationPasswordMatches = Observe(call, MailAuthenticationStage.ApplicationPasswordVerification, () => PasswordHasher.Verify(
            password,
            applicationPassword?.PasswordHash ?? DummyPasswordHash));
        if (applicationPassword is null || !applicationPasswordMatches)
            return null;

        applicationPassword.LastUsedAt = DateTime.UtcNow;
        await ObserveAsync(call, MailAuthenticationStage.ApplicationPasswordUsageUpdate,
            () => _database.SaveChangesAsync(cancellationToken)).ConfigureAwait(false);

        return new AuthenticatedMailUser(userId, username);
    }

    private Task<T> ObserveAsync<T>(Guid call, MailAuthenticationStage stage, Func<Task<T>> action) =>
        _observer is null ? action() : ObserveStageAsync(call, stage, action);

    private async Task<T> ObserveStageAsync<T>(Guid call, MailAuthenticationStage stage, Func<Task<T>> action)
    {
        Record(call, stage, MailAuthenticationStagePhase.Entered);
        try
        {
            var result = await action().ConfigureAwait(false);
            Record(call, stage, MailAuthenticationStagePhase.Returned);
            return result;
        }
        catch (OperationCanceledException)
        {
            Record(call, stage, MailAuthenticationStagePhase.Cancelled);
            throw;
        }
        catch
        {
            Record(call, stage, MailAuthenticationStagePhase.Faulted);
            throw;
        }
    }

    private T Observe<T>(Guid call, MailAuthenticationStage stage, Func<T> action)
    {
        if (_observer is null) return action();
        Record(call, stage, MailAuthenticationStagePhase.Entered);
        try
        {
            var result = action();
            Record(call, stage, MailAuthenticationStagePhase.Returned);
            return result;
        }
        catch (OperationCanceledException)
        {
            Record(call, stage, MailAuthenticationStagePhase.Cancelled);
            throw;
        }
        catch
        {
            Record(call, stage, MailAuthenticationStagePhase.Faulted);
            throw;
        }
    }

    private void Record(Guid call, MailAuthenticationStage stage, MailAuthenticationStagePhase phase)
    {
        if (_observer is null) return;
        try
        {
            _observer.Record(call, stage, phase);
        }
#pragma warning disable CA1031 // Secondary diagnostic sink failures cannot replace authentication's result or original fault; fatal failures propagate.
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
        }
#pragma warning restore CA1031
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
