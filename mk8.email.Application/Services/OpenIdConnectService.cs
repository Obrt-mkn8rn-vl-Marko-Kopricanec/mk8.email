using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;

namespace mk8.email.Application.Services;

public sealed class OpenIdConnectService : IOpenIdConnectService
{
    private readonly EnvironmentConfig environment;
    private readonly RSAParameters? privateKey;
    private readonly OpenIdConnectPublicKey? publicKey;

    public OpenIdConnectService(EnvironmentConfig environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        this.environment = environment;
        if (!environment.OAuth.EnableOpenIdConnect)
            return;

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(environment.OAuth.SigningKey ?? string.Empty);
            var parameters = rsa.ExportParameters(includePrivateParameters: true);
            if (parameters.Modulus is not { Length: >= 256 }
                || parameters.Exponent is not { Length: > 0 }
                || parameters.D is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "The OpenID Connect signing key must be an RSA private key of at least 2048 bits.");
            }

            var modulus = Base64UrlEncode(parameters.Modulus);
            var exponent = Base64UrlEncode(parameters.Exponent);
            var thumbprintJson = Encoding.UTF8.GetBytes(
                $"{{\"e\":\"{exponent}\",\"kty\":\"RSA\",\"n\":\"{modulus}\"}}");
            var keyId = Base64UrlEncode(SHA256.HashData(thumbprintJson));
            privateKey = parameters;
            publicKey = new("RSA", "sig", keyId, "RS256", modulus, exponent);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The OpenID Connect signing key is not a valid RSA private key.",
                exception);
        }
    }

    public OpenIdConnectPublicKey GetPublicKey() =>
        publicKey ?? throw new InvalidOperationException("OpenID Connect is not enabled.");

    public string CreateIdToken(
        Guid userId,
        string username,
        string clientId,
        string accessToken,
        DateTime authenticatedAt,
        DateTime issuedAt,
        string? nonce,
        IReadOnlyCollection<string> scopes)
    {
        if (!scopes.Contains("openid", StringComparer.Ordinal))
            throw new ArgumentException("The openid scope is required.", nameof(scopes));
        var key = privateKey
            ?? throw new InvalidOperationException("OpenID Connect is not enabled.");
        var publishedKey = GetPublicKey();
        var issuer = environment.OAuth.GetPublicBaseUri(
            environment.Smtp.Hostname,
            environment.Jmap.PublicBaseUrl).AbsoluteUri.TrimEnd('/');
        var issued = ToUnixTimeSeconds(issuedAt);
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iss"] = issuer,
            ["sub"] = userId.ToString("D"),
            ["aud"] = clientId,
            ["exp"] = issued + checked(environment.OAuth.IdTokenMinutes * 60L),
            ["iat"] = issued,
            ["auth_time"] = ToUnixTimeSeconds(authenticatedAt),
            ["at_hash"] = AccessTokenHash(accessToken),
        };
        if (!string.IsNullOrEmpty(nonce))
            payload["nonce"] = nonce;
        if (scopes.Contains("email", StringComparer.Ordinal))
        {
            payload["email"] = username;
            payload["email_verified"] = true;
        }
        if (scopes.Contains("profile", StringComparer.Ordinal))
            payload["preferred_username"] = username;

        var header = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["alg"] = "RS256",
            ["kid"] = publishedKey.KeyId,
            ["typ"] = "JWT",
        };
        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        using var rsa = RSA.Create();
        rsa.ImportParameters(key);
        var signature = rsa.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{encodedHeader}.{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    private static string AccessTokenHash(string accessToken)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncode(digest.AsSpan(0, digest.Length / 2));
    }

    private static long ToUnixTimeSeconds(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
