using System.Security.Cryptography;
using System.Text;

namespace mk8.email.Contracts.Protocol;

public static class OAuthProtocolValues
{
    public static readonly IReadOnlySet<string> SupportedScopes = new HashSet<string>(
        ["offline_access", "imap", "smtp", "pop", "jmap", "dav", "sieve", "openid", "profile", "email"],
        StringComparer.Ordinal);

    public static bool TryNormalizeScopes(
        IEnumerable<string> scopes,
        out string[] normalized)
    {
        // OAuth scope tokens are specified and exchanged in lower case.
#pragma warning disable CA1308
        normalized = scopes
            .SelectMany(scope => scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(scope => scope.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
#pragma warning restore CA1308
        return normalized.Length is > 0 and <= 16
            && normalized.All(SupportedScopes.Contains)
            && normalized.Contains("offline_access", StringComparer.Ordinal);
    }

    public static bool IsValidPkceChallenge(string value) =>
        value is not null && value.Length == 43 && value.All(IsBase64UrlCharacter);

    public static bool IsValidPkceVerifier(string value) =>
        value is not null && value.Length is >= 43 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    public static string CreatePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool IsAllowedRedirectUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || uri.UserInfo.Length > 0
            || uri.Fragment.Length > 0
            || uri.Query.Length > 0
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return false;
        }

        return uri.Host is "127.0.0.1" or "::1" or "[::1]" or "localhost";
    }

    private static bool IsBase64UrlCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}
