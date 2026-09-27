using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class UnavailableJmapPushPresentationClient : IJmapPushPresentationClient
{
    public Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The JMAP Web Push presentation client is unavailable.");

    public Task<WebPushSendOutcome> SendAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        JmapPushMessage payload,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The JMAP Web Push presentation client is unavailable.");

    public Task EnqueueVerificationAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        JmapPushMessage payload,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The JMAP Web Push presentation client is unavailable.");
}
