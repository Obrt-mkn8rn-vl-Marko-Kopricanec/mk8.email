using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

public interface IJmapPushPresentationClient
{
    Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken);

    Task<WebPushSendOutcome> SendAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload,
        CancellationToken cancellationToken);

    Task EnqueueVerificationAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableJmapPushPresentationClient : IJmapPushPresentationClient
{
    public Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The JMAP Web Push presentation client is unavailable.");

    public Task<WebPushSendOutcome> SendAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The JMAP Web Push presentation client is unavailable.");

    public Task EnqueueVerificationAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The JMAP Web Push presentation client is unavailable.");
}

internal static class JmapPushPresentationPayload
{
    public static byte[] Serialize(JsonObject value) =>
        Encoding.UTF8.GetBytes(value.ToJsonString(JmapJson.SerializerOptions));
}

internal static class JmapPushKeyValidator
{
    public static bool TryValidate(string p256dh, string auth)
    {
        try
        {
            var publicKey = DecodeBase64Url(p256dh);
            var authenticationSecret = DecodeBase64Url(auth);
            if (publicKey.Length != 65 || publicKey[0] != 4
                || authenticationSecret.Length != 16)
                return false;
            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = publicKey.AsSpan(1, 32).ToArray(),
                    Y = publicKey.AsSpan(33, 32).ToArray(),
                },
            });
            _ = peer.ExportParameters(false);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
            throw new FormatException("Invalid Web Push key encoding.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid Web Push key length."),
        };
        return Convert.FromBase64String(padded);
    }
}
