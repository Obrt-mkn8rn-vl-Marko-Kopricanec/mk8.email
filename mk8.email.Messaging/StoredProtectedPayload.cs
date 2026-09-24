using mk8.email.Contracts.Storage;

namespace mk8.email.Messaging;

internal sealed record StoredProtectedPayload(
    string KeyId,
    byte[]? InlineCiphertext,
    LargeObjectReference? LargeObject,
    byte[] Nonce,
    byte[] Tag,
    string Sha256,
    long Length,
    bool LargeObjectCreated = false)
{
    public bool IsLargeObject => LargeObject is not null;
}
