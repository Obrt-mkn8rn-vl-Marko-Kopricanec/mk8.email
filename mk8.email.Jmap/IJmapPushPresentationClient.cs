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
        JmapPushMessage payload,
        CancellationToken cancellationToken);

    Task EnqueueVerificationAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        JmapPushMessage payload,
        CancellationToken cancellationToken);
}
