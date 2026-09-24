using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MfaService(
    EmailDbContext database,
    EnvironmentConfig environment) : IMfaService
{
    private const int SecretBytes = 20;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] DummyHash = new byte[32];

    public async Task<MfaEnrollmentResult> BeginTotpEnrollmentAsync(
        string username,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEncryptionKey(out var encryptionKey))
            return EnrollmentFailure("TOTP MFA is not enabled.");
        var normalized = NormalizeUsername(username);
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return EnrollmentFailure("The account address is not valid.");
        if (normalizedName.Length is < 1 or > 128)
            return EnrollmentFailure("The authenticator name must contain from 1 through 128 characters.");

        var user = await FindActiveUserAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return EnrollmentFailure("The account does not exist or is not active.");

        var credential = await database.MfaTotpCredentials
            .Include(candidate => candidate.RecoveryCodes)
            .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id, cancellationToken).ConfigureAwait(false);
        if (credential is { VerifiedAt: not null, RevokedAt: null })
            return EnrollmentFailure("The account already has an active authenticator.");

        var now = DateTime.UtcNow;
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        if (credential is null)
        {
            credential = new MfaTotpCredentialDB
            {
                Id = Guid.CreateVersion7(),
                UserId = user.Id,
            };
            database.MfaTotpCredentials.Add(credential);
        }
        else
        {
            database.MfaRecoveryCodes.RemoveRange(credential.RecoveryCodes);
        }

        var encrypted = EncryptSecret(secret, credential.Id, user.Id, encryptionKey);
        credential.Name = normalizedName;
        credential.EncryptedSecret = encrypted.Ciphertext;
        credential.EncryptionNonce = encrypted.Nonce;
        credential.EncryptionTag = encrypted.Tag;
        credential.CreatedAt = now;
        credential.VerifiedAt = null;
        credential.LastUsedAt = null;
        credential.LastAcceptedTimeStep = null;
        credential.RevokedAt = null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var encodedSecret = TotpMfa.EncodeSecret(secret);
        var issuer = environment.Mfa.Issuer ?? string.Empty;
        var label = Uri.EscapeDataString($"{issuer}:{normalized}");
        var provisioningUri =
            $"otpauth://totp/{label}?secret={encodedSecret}"
            + $"&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
        return new(
            true,
            "The pending authenticator was created. Confirm one code before it is enforced.",
            encodedSecret,
            provisioningUri);
    }

    public async Task<MfaRecoveryCodesResult> ConfirmTotpEnrollmentAsync(
        string username,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEncryptionKey(out var encryptionKey))
            return RecoveryFailure("TOTP MFA is not enabled.");
        var normalized = NormalizeUsername(username);
        if (normalized.Length == 0)
            return RecoveryFailure("The account address is not valid.");

        var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var credential = await database.MfaTotpCredentials
            .Include(candidate => candidate.User)
            .Include(candidate => candidate.RecoveryCodes)
            .SingleOrDefaultAsync(
                candidate => candidate.User.Username == normalized
                    && candidate.VerifiedAt == null
                    && candidate.RevokedAt == null,
                cancellationToken).ConfigureAwait(false);
            if (credential is null)
                return RecoveryFailure("No pending authenticator enrollment exists.");

            byte[] secret;
            try
            {
                secret = DecryptSecret(credential, encryptionKey);
            }
            catch (CryptographicException)
            {
                return RecoveryFailure("The authenticator enrollment cannot be decrypted.");
            }
            if (!TotpMfa.TryVerify(
                    secret,
                    code?.Trim() ?? string.Empty,
                    DateTime.UtcNow,
                    lastAcceptedTimeStep: null,
                    out var acceptedTimeStep))
            {
                return RecoveryFailure("The authenticator code is not valid.");
            }

            var now = DateTime.UtcNow;
            credential.VerifiedAt = now;
            credential.LastUsedAt = now;
            credential.LastAcceptedTimeStep = acceptedTimeStep;
            database.MfaRecoveryCodes.RemoveRange(credential.RecoveryCodes);
            var recoveryCodes = CreateRecoveryCodes(credential, now);
            await RevokeOAuthCredentialsAsync(credential.UserId, now, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                true,
                "TOTP MFA is enabled. Store the recovery codes securely; they will not be shown again.",
                recoveryCodes);
        }
    }

    public async Task<MfaRecoveryCodesResult> RegenerateRecoveryCodesAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (!environment.Mfa.EnableTotp)
            return RecoveryFailure("TOTP MFA is not enabled.");
        var normalized = NormalizeUsername(username);
        var credential = await database.MfaTotpCredentials
            .Include(candidate => candidate.User)
            .Include(candidate => candidate.RecoveryCodes)
            .SingleOrDefaultAsync(
                candidate => candidate.User.Username == normalized
                    && candidate.VerifiedAt != null
                    && candidate.RevokedAt == null,
                cancellationToken).ConfigureAwait(false);
        if (credential is null)
            return RecoveryFailure("The account does not have an active authenticator.");

        database.MfaRecoveryCodes.RemoveRange(credential.RecoveryCodes);
        var recoveryCodes = CreateRecoveryCodes(credential, DateTime.UtcNow);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(
            true,
            "New recovery codes were created. Every prior recovery code is invalid.",
            recoveryCodes);
    }

    public async Task<bool> DisableTotpAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        if (normalized.Length == 0)
            return false;
        var credential = await database.MfaTotpCredentials
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(
                candidate => candidate.User.Username == normalized,
                cancellationToken).ConfigureAwait(false);
        if (credential is null)
            return false;
        if (credential.RevokedAt is null)
        {
            var now = DateTime.UtcNow;
            credential.RevokedAt = now;
            await RevokeOAuthCredentialsAsync(credential.UserId, now, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<MfaStatus> GetStatusAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        if (normalized.Length == 0)
            return new(false, null, null, null, null, 0);
        var credential = await database.MfaTotpCredentials
            .AsNoTracking()
            .Where(candidate => candidate.User.Username == normalized)
            .Select(candidate => new
            {
                candidate.Name,
                candidate.CreatedAt,
                candidate.VerifiedAt,
                candidate.LastUsedAt,
                candidate.RevokedAt,
                Remaining = candidate.RecoveryCodes.Count(code => code.UsedAt == null),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var enrolled = credential is { VerifiedAt: not null, RevokedAt: null };
        return credential is null
            ? new(false, null, null, null, null, 0)
            : new(
                enrolled,
                credential.Name,
                credential.CreatedAt,
                credential.VerifiedAt,
                credential.LastUsedAt,
                enrolled ? credential.Remaining : 0);
    }

    public async Task<MfaVerificationResult> VerifyForAuthenticationAsync(
        Guid userId,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (!environment.Mfa.EnableTotp)
            return MfaVerificationResult.NotRequired;
        if (!TryGetEncryptionKey(out var encryptionKey))
            return MfaVerificationResult.Failed;

        var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
            var credential = await database.MfaTotpCredentials
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == userId
                    && candidate.VerifiedAt != null
                    && candidate.RevokedAt == null,
                cancellationToken).ConfigureAwait(false);
            if (credential is null)
                return MfaVerificationResult.NotRequired;

            var normalizedCode = code?.Trim() ?? string.Empty;
            var now = DateTime.UtcNow;
            var verified = false;
            if (normalizedCode.Length == 6
                && normalizedCode.All(character => character is >= '0' and <= '9'))
            {
                try
                {
                    var secret = DecryptSecret(credential, encryptionKey);
                    verified = TotpMfa.TryVerify(
                        secret,
                        normalizedCode,
                        now,
                        credential.LastAcceptedTimeStep,
                        out var acceptedTimeStep);
                    if (verified)
                        credential.LastAcceptedTimeStep = acceptedTimeStep;
                }
                catch (CryptographicException)
                {
                    verified = false;
                }
            }
            else if (TryParseRecoveryCodeId(normalizedCode, out var recoveryCodeId))
            {
                var recoveryCode = await database.MfaRecoveryCodes
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == recoveryCodeId
                            && candidate.CredentialId == credential.Id
                            && candidate.UsedAt == null,
                        cancellationToken).ConfigureAwait(false);
                verified = RecoveryCodeHashMatches(normalizedCode, recoveryCode?.CodeHash);
                if (verified)
                    recoveryCode!.UsedAt = now;
            }

            if (!verified)
                return MfaVerificationResult.Failed;

            credential.LastUsedAt = now;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return MfaVerificationResult.Succeeded;
        }
    }

    private List<string> CreateRecoveryCodes(MfaTotpCredentialDB credential, DateTime createdAt)
    {
        var values = new List<string>(environment.Mfa.RecoveryCodeCount);
        for (var index = 0; index < environment.Mfa.RecoveryCodeCount; index++)
        {
            var id = Guid.CreateVersion7();
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var value = $"mk8_rc_{id:N}_{secret}";
            database.MfaRecoveryCodes.Add(new MfaRecoveryCodeDB
            {
                Id = id,
                Credential = credential,
                CredentialId = credential.Id,
                CodeHash = HashRecoveryCode(value),
                CreatedAt = createdAt,
            });
            values.Add(value);
        }
        return values;
    }

    private async Task RevokeOAuthCredentialsAsync(
        Guid userId,
        DateTime revokedAt,
        CancellationToken cancellationToken)
    {
        var grants = await database.OAuthGrants
            .Where(grant => grant.UserId == userId && grant.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var grant in grants)
            grant.RevokedAt = revokedAt;
        var tokens = await database.OAuthTokens
            .Where(token => token.Grant.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var token in tokens)
            token.RevokedAt = revokedAt;
        var authorizationCodes = await database.OAuthAuthorizationCodes
            .Where(code => code.UserId == userId && code.ConsumedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var authorizationCode in authorizationCodes)
            authorizationCode.ConsumedAt = revokedAt;
    }

    private async Task<UserDB?> FindActiveUserAsync(
        string username,
        CancellationToken cancellationToken)
    {
        var separator = username.LastIndexOf('@');
        var domain = username[(separator + 1)..];
        return await database.Users.SingleOrDefaultAsync(user =>
            user.Username == username
            && user.IsActive
            && user.CompanyId != null
            && user.Company != null
            && user.Company.IsActive
            && database.Addresses.Any(address =>
                address.CompanyId == user.CompanyId
                && address.Domain == domain
                && address.IsActive),
            cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetEncryptionKey(out byte[] key)
    {
        key = [];
        if (!environment.Mfa.EnableTotp)
            return false;
        try
        {
            key = Convert.FromBase64String(environment.Mfa.EncryptionKey ?? string.Empty);
            return key.Length == 32;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            return false;
        }
    }

    private static EncryptedSecret EncryptSecret(
        byte[] secret,
        Guid credentialId,
        Guid userId,
        byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[secret.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, secret, ciphertext, tag, AssociatedData(credentialId, userId));
        return new(ciphertext, nonce, tag);
    }

    private static byte[] DecryptSecret(MfaTotpCredentialDB credential, byte[] key)
    {
        if (credential.EncryptedSecret.Length != SecretBytes
            || credential.EncryptionNonce.Length != NonceBytes
            || credential.EncryptionTag.Length != TagBytes)
        {
            throw new CryptographicException("The encrypted TOTP secret is malformed.");
        }
        var secret = new byte[SecretBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(
            credential.EncryptionNonce,
            credential.EncryptedSecret,
            credential.EncryptionTag,
            secret,
            AssociatedData(credential.Id, credential.UserId));
        return secret;
    }

    private static byte[] AssociatedData(Guid credentialId, Guid userId)
    {
        var associatedData = new byte[32];
        credentialId.TryWriteBytes(associatedData);
        userId.TryWriteBytes(associatedData.AsSpan(16));
        return associatedData;
    }

    private static bool TryParseRecoveryCodeId(string value, out Guid id)
    {
        const string prefix = "mk8_rc_";
        const int identifierLength = 32;
        const int secretLength = 32;
        id = Guid.Empty;
        return value.Length == prefix.Length + identifierLength + 1 + secretLength
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value[prefix.Length + identifierLength] == '_'
            && value[(prefix.Length + identifierLength + 1)..].All(IsBase64UrlCharacter)
            && Guid.TryParseExact(
                value.AsSpan(prefix.Length, identifierLength),
                "N",
                out id);
    }

    private static bool RecoveryCodeHashMatches(string value, byte[]? expectedHash)
    {
        var expected = expectedHash is { Length: 32 } ? expectedHash : DummyHash;
        return CryptographicOperations.FixedTimeEquals(HashRecoveryCode(value), expected);
    }

    private static byte[] HashRecoveryCode(string value) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(value));

    private static bool IsBase64UrlCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static string NormalizeUsername(string username) =>
        SmtpAddress.TryNormalize(username, allowEmpty: false, out var normalized)
            ? normalized
            : string.Empty;

    private static MfaEnrollmentResult EnrollmentFailure(string message) => new(false, message);
    private static MfaRecoveryCodesResult RecoveryFailure(string message) => new(false, message);
    private sealed record EncryptedSecret(byte[] Ciphertext, byte[] Nonce, byte[] Tag);
}
