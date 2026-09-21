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

public sealed class OAuthTokenService(
    EmailDbContext database,
    EnvironmentConfig environment,
    IOpenIdConnectService openIdConnect) : IOAuthTokenService
{
    public const string AccessTokenType = "access";
    public const string RefreshTokenType = "refresh";
    private static readonly byte[] DummyHash = new byte[32];

    public OAuthTokenService(EmailDbContext database, EnvironmentConfig environment)
        : this(database, environment, new OpenIdConnectService(environment))
    {
    }

    public async Task<OAuthTokenPair?> CreateGrantAsync(
        Guid userId,
        string clientId,
        string deviceName,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = clientId?.Trim() ?? string.Empty;
        var normalizedDeviceName = deviceName?.Trim() ?? string.Empty;
        var normalizedScopes = NormalizeScopes(scopes);
        if (normalizedClientId.Length is < 1 or > 128
            || normalizedDeviceName.Length is < 1 or > 128
            || normalizedScopes is null
            || normalizedScopes.Contains("openid", StringComparer.Ordinal)
                && !environment.OAuth.EnableOpenIdConnect
            || normalizedScopes.Any(scope => scope is "email" or "profile")
                && !normalizedScopes.Contains("openid", StringComparer.Ordinal))
        {
            return null;
        }

        var user = await FindActiveUserAsync(userId, cancellationToken);
        if (user is null)
            return null;

        var now = DateTime.UtcNow;
        var grant = new OAuthGrantDB
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            User = user,
            ClientId = normalizedClientId,
            DeviceName = normalizedDeviceName,
            Scopes = normalizedScopes,
            CreatedAt = now,
        };
        database.OAuthGrants.Add(grant);
        var issued = IssueTokenPair(grant, now);
        await database.SaveChangesAsync(cancellationToken);
        return issued.Pair;
    }

    public async Task<OAuthTokenPair?> RefreshAsync(
        string refreshToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseTokenId(refreshToken, "mk8_rt_", out var tokenId))
            return null;

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var token = await database.OAuthTokens
            .Include(candidate => candidate.Grant)
            .ThenInclude(grant => grant.User)
            .ThenInclude(user => user.Company)
            .SingleOrDefaultAsync(candidate => candidate.Id == tokenId, cancellationToken);
        var hashMatches = TokenHashMatches(refreshToken, token?.TokenHash);
        if (token is null || !hashMatches || token.TokenType != RefreshTokenType)
            return null;

        var now = DateTime.UtcNow;
        if (token.RevokedAt is not null)
        {
            await RevokeGrantEntityAsync(token.Grant, now, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (token.ExpiresAt <= now
            || token.Grant.RevokedAt is not null
            || !string.Equals(token.Grant.ClientId, clientId, StringComparison.Ordinal)
            || !await IsUserActiveAsync(token.Grant.User, cancellationToken))
        {
            token.RevokedAt ??= now;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var activeAccessTokens = await database.OAuthTokens
            .Where(candidate => candidate.GrantId == token.GrantId
                && candidate.TokenType == AccessTokenType
                && candidate.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var activeAccessToken in activeAccessTokens)
            activeAccessToken.RevokedAt = now;

        token.RevokedAt = now;
        token.LastUsedAt = now;
        token.Grant.LastUsedAt = now;
        var issued = IssueTokenPair(token.Grant, now);
        token.ReplacedByTokenId = issued.RefreshTokenId;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return issued.Pair;
    }

    public async Task<AuthenticatedMailUser?> AuthenticateAccessTokenAsync(
        string accessToken,
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        var identity = await AuthenticateIdentityAsync(
            accessToken,
            requiredScope,
            cancellationToken);
        return identity is null
            ? null
            : new(identity.UserId, identity.Username);
    }

    public async Task<OAuthAccessTokenIdentity?> AuthenticateIdentityAsync(
        string accessToken,
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        if (!OAuthProtocolValues.SupportedScopes.Contains(requiredScope)
            || !TryParseTokenId(accessToken, "mk8_at_", out var tokenId))
        {
            return null;
        }

        var token = await database.OAuthTokens
            .Include(candidate => candidate.Grant)
            .ThenInclude(grant => grant.User)
            .ThenInclude(user => user.Company)
            .SingleOrDefaultAsync(candidate => candidate.Id == tokenId, cancellationToken);
        var hashMatches = TokenHashMatches(accessToken, token?.TokenHash);
        var now = DateTime.UtcNow;
        if (token is null
            || !hashMatches
            || token.TokenType != AccessTokenType
            || token.RevokedAt is not null
            || token.ExpiresAt <= now
            || token.Grant.RevokedAt is not null
            || !token.Grant.Scopes.Contains(requiredScope, StringComparer.Ordinal)
            || !await IsUserActiveAsync(token.Grant.User, cancellationToken))
        {
            return null;
        }

        token.LastUsedAt = now;
        token.Grant.LastUsedAt = now;
        await database.SaveChangesAsync(cancellationToken);
        return new(
            token.Grant.User.Id,
            token.Grant.User.Username,
            token.Grant.Scopes);
    }

    public async Task<IReadOnlyList<OAuthGrantSummary>> ListGrantsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await database.OAuthGrants
            .AsNoTracking()
            .Where(grant => grant.UserId == userId)
            .OrderByDescending(grant => grant.CreatedAt)
            .Select(grant => new OAuthGrantSummary(
                grant.Id,
                grant.ClientId,
                grant.DeviceName,
                grant.Scopes,
                grant.CreatedAt,
                grant.LastUsedAt,
                grant.RevokedAt))
            .ToListAsync(cancellationToken);

    public async Task<bool> RevokeGrantAsync(
        Guid userId,
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        var grant = await database.OAuthGrants
            .SingleOrDefaultAsync(
                candidate => candidate.Id == grantId && candidate.UserId == userId,
                cancellationToken);
        if (grant is null)
            return false;

        await RevokeGrantEntityAsync(grant, DateTime.UtcNow, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RevokeTokenAsync(
        string token,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var parsed = TryParseTokenId(token, "mk8_at_", out var tokenId)
            || TryParseTokenId(token, "mk8_rt_", out tokenId);
        if (!parsed)
            return;

        var stored = await database.OAuthTokens
            .Include(candidate => candidate.Grant)
            .SingleOrDefaultAsync(candidate => candidate.Id == tokenId, cancellationToken);
        if (stored is null
            || !TokenHashMatches(token, stored.TokenHash)
            || !string.Equals(stored.Grant.ClientId, clientId, StringComparison.Ordinal))
        {
            return;
        }

        await RevokeGrantEntityAsync(stored.Grant, DateTime.UtcNow, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }

    private IssuedPair IssueTokenPair(OAuthGrantDB grant, DateTime now)
    {
        var access = CreateToken(grant, AccessTokenType, "mk8_at_", now,
            now.AddMinutes(environment.OAuth.AccessTokenMinutes));
        var refresh = CreateToken(grant, RefreshTokenType, "mk8_rt_", now,
            now.AddDays(environment.OAuth.RefreshTokenDays));
        var idToken = grant.Scopes.Contains("openid", StringComparer.Ordinal)
            ? openIdConnect.CreateIdToken(
                grant.UserId,
                grant.User.Username,
                grant.ClientId,
                access.Value,
                grant.CreatedAt,
                now,
                nonce: null,
                grant.Scopes)
            : null;
        return new(
            new OAuthTokenPair(
                grant.Id,
                access.Value,
                refresh.Value,
                checked(environment.OAuth.AccessTokenMinutes * 60),
                string.Join(' ', grant.Scopes),
                idToken),
            refresh.Entity.Id);
    }

    private TokenValue CreateToken(
        OAuthGrantDB grant,
        string tokenType,
        string prefix,
        DateTime createdAt,
        DateTime expiresAt)
    {
        var id = Guid.CreateVersion7();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var value = $"{prefix}{id:N}_{secret}";
        var entity = new OAuthTokenDB
        {
            Id = id,
            Grant = grant,
            GrantId = grant.Id,
            TokenType = tokenType,
            TokenHash = HashToken(value),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
        database.OAuthTokens.Add(entity);
        return new(entity, value);
    }

    private async Task RevokeGrantEntityAsync(
        OAuthGrantDB grant,
        DateTime revokedAt,
        CancellationToken cancellationToken)
    {
        grant.RevokedAt ??= revokedAt;
        var tokens = await database.OAuthTokens
            .Where(token => token.GrantId == grant.Id && token.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
            token.RevokedAt = revokedAt;
    }

    private async Task<UserDB?> FindActiveUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await database.Users
            .Include(candidate => candidate.Company)
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        return user is not null && await IsUserActiveAsync(user, cancellationToken)
            ? user
            : null;
    }

    private async Task<bool> IsUserActiveAsync(
        UserDB user,
        CancellationToken cancellationToken)
    {
        if (!user.IsActive || user.CompanyId is null || user.Company?.IsActive != true)
            return false;

        var separator = user.Username.LastIndexOf('@');
        if (separator <= 0 || separator == user.Username.Length - 1)
            return false;
        var domain = user.Username[(separator + 1)..];
        return await database.Addresses.AnyAsync(address =>
            address.CompanyId == user.CompanyId
            && address.Domain == domain
            && address.IsActive,
            cancellationToken);
    }

    private static string[]? NormalizeScopes(IReadOnlyCollection<string> scopes)
    {
        if (scopes.Count is < 1 or > 16)
            return null;
        var normalized = scopes
            .Select(scope => scope.Trim().ToLowerInvariant())
            .Where(scope => scope.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length > 0 && normalized.All(OAuthProtocolValues.SupportedScopes.Contains)
            ? normalized
            : null;
    }

    private static bool TryParseTokenId(string value, string prefix, out Guid id)
    {
        const int identifierLength = 32;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length <= prefix.Length + identifierLength + 1
            || value[prefix.Length + identifierLength] != '_')
        {
            return false;
        }
        return Guid.TryParseExact(
            value.AsSpan(prefix.Length, identifierLength),
            "N",
            out id);
    }

    private static bool TokenHashMatches(string value, byte[]? expectedHash)
    {
        var actualHash = HashToken(value);
        var expected = expectedHash is { Length: 32 } ? expectedHash : DummyHash;
        return CryptographicOperations.FixedTimeEquals(actualHash, expected);
    }

    private static byte[] HashToken(string value) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(value));

    private sealed record TokenValue(OAuthTokenDB Entity, string Value);
    private sealed record IssuedPair(OAuthTokenPair Pair, Guid RefreshTokenId);
}
