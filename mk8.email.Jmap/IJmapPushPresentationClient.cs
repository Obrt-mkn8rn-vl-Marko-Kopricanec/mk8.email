using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

public interface IJmapPushPresentationClient
{
    [SuppressMessage("Design", "CA1054", Justification = "Untrusted user URL text is safety-checked before constructing a Uri.")]
    Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken);

    [SuppressMessage("Design", "CA1054", Justification = "The durable presentation request carries the original validated URL text.")]
    Task<WebPushSendOutcome> SendAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        JmapPushMessage payload,
        CancellationToken cancellationToken);

    [SuppressMessage("Design", "CA1054", Justification = "The durable presentation request carries the original validated URL text.")]
    Task EnqueueVerificationAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        JmapPushMessage payload,
        CancellationToken cancellationToken);

    [SuppressMessage("Design", "CA1054", Justification = "The durable presentation request carries the original validated URL text.")]
    ApplicationRequest? CreateVerificationRequest(string url, string? keysJson, DateTime expiresAt, JmapPushMessage payload);
}
