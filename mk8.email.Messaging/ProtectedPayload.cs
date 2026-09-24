namespace mk8.email.Messaging;

// Existing ciphertext, nonce, and tag buffers are byte arrays in the published API.
#pragma warning disable CA1819
public sealed record ProtectedPayload(
    string KeyId,
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] Tag);
#pragma warning restore CA1819
