using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class OAuthAuthorizationService(
    EmailDbContext database,
    IOAuthTokenService tokenService,
    EnvironmentConfig environment) : IOAuthAuthorizationService
{
    private static readonly byte[] DummyHash = new byte[32];

    public async Task<string?> CreateAuthorizationCodeAsync(
        Guid userId,
        string clientId,
        string redirectUri,
        string deviceName,
        IReadOnlyCollection<string> scopes,
        string codeChallenge,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(clientId, environment.OAuth.ClientId, StringComparison.Ordinal)
            || !OAuthProtocolValues.IsAllowedRedirectUri(redirectUri)
            || !OAuthProtocolValues.TryNormalizeScopes(scopes, out var normalizedScopes)
            || !OAuthProtocolValues.IsValidPkceChallenge(codeChallenge)
            || string.IsNullOrWhiteSpace(deviceName)
            || deviceName.Trim().Length > 128)
        {
            return null;
        }

        var userExists = await database.Users.AnyAsync(
            user => user.Id == userId && user.IsActive,
            cancellationToken);
        if (!userExists)
            return null;

        var now = DateTime.UtcNow;
        var staleCodes = await database.OAuthAuthorizationCodes
            .Where(code => code.ExpiresAt < now.AddDays(-1))
            .OrderBy(code => code.ExpiresAt)
            .Take(1000)
            .ToListAsync(cancellationToken);
        database.OAuthAuthorizationCodes.RemoveRange(staleCodes);

        var id = Guid.CreateVersion7();
        var codeValue = CreateOpaqueValue("mk8_ac_", id);
        database.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCodeDB
        {
            Id = id,
            UserId = userId,
            ClientId = clientId,
            RedirectUri = redirectUri,
            DeviceName = deviceName.Trim(),
            Scopes = normalizedScopes,
            CodeChallenge = codeChallenge,
            CodeHash = HashAscii(codeValue),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(environment.OAuth.AuthorizationCodeMinutes),
        });
        await database.SaveChangesAsync(cancellationToken);
        return codeValue;
    }

    public async Task<OAuthTokenPair?> RedeemAuthorizationCodeAsync(
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCodeId(code, out var codeId)
            || !OAuthProtocolValues.IsValidPkceVerifier(codeVerifier))
        {
            return null;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var authorizationCode = await database.OAuthAuthorizationCodes
            .SingleOrDefaultAsync(candidate => candidate.Id == codeId, cancellationToken);
        var codeMatches = FixedTimeHashMatches(code, authorizationCode?.CodeHash);
        var verifierChallenge = OAuthProtocolValues.CreatePkceChallenge(codeVerifier);
        var challengeMatches = authorizationCode is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(verifierChallenge),
                Encoding.ASCII.GetBytes(authorizationCode.CodeChallenge));
        var now = DateTime.UtcNow;
        if (authorizationCode is null
            || !codeMatches
            || !challengeMatches
            || authorizationCode.ConsumedAt is not null
            || authorizationCode.ExpiresAt <= now
            || !string.Equals(authorizationCode.ClientId, clientId, StringComparison.Ordinal)
            || !string.Equals(authorizationCode.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            return null;
        }

        authorizationCode.ConsumedAt = now;
        var pair = await tokenService.CreateGrantAsync(
            authorizationCode.UserId,
            authorizationCode.ClientId,
            authorizationCode.DeviceName,
            authorizationCode.Scopes,
            cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pair;
    }

    private static string CreateOpaqueValue(string prefix, Guid id)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{prefix}{id:N}_{secret}";
    }

    private static bool TryParseCodeId(string value, out Guid id)
    {
        const string prefix = "mk8_ac_";
        const int identifierLength = 32;
        id = Guid.Empty;
        return !string.IsNullOrEmpty(value)
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value.Length > prefix.Length + identifierLength + 1
            && value[prefix.Length + identifierLength] == '_'
            && Guid.TryParseExact(
                value.AsSpan(prefix.Length, identifierLength),
                "N",
                out id);
    }

    private static bool FixedTimeHashMatches(string value, byte[]? expectedHash)
    {
        var expected = expectedHash is { Length: 32 } ? expectedHash : DummyHash;
        return CryptographicOperations.FixedTimeEquals(HashAscii(value), expected);
    }

    private static byte[] HashAscii(string value) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(value));
}
